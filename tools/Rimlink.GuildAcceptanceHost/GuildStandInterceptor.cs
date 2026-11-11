using Grpc.Core;
using Grpc.Core.Interceptors;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Rimlink.Protocol.V1;

namespace Rimlink.GuildAcceptanceHost;

internal sealed class GuildStandInterceptor(GuildAcceptanceComposition composition):Interceptor
{
    public override Task<TResponse> UnaryServerHandler<TRequest,TResponse>(TRequest request,ServerCallContext context,UnaryServerMethod<TRequest,TResponse> continuation)
    {
        if(composition.Observe() is not{AutomatedPrerequisitesVerified:true,IsolationVerified:true})throw new RpcException(new(StatusCode.Unavailable,"isolated acceptance evidence unavailable"));
        var stand=composition.Bootstrap.Stand;
        if(request is RegisterRequest && composition.SetupClockAdvanced)
            throw new RpcException(new(StatusCode.FailedPrecondition,"both isolated identities must be registered before advancing setup time"));
        string? login=request switch
        {
            RegisterRequest r=>r.Login,
            LoginRequest r when r.CredentialCase==LoginRequest.CredentialOneofCase.Password=>r.Password.Login,
            LoginRequest=>throw new RpcException(new(StatusCode.PermissionDenied,"isolated acceptance requires its own password identities")),
            _=>null
        };
        if(login is not null && login!=stand.AttackerLogin && login!=stand.DefenderLogin)throw new RpcException(new(StatusCode.PermissionDenied,"identity outside isolated acceptance allowlist"));
        return continuation(request,context);
    }
}

internal sealed class GuildStandRequestFilter(GuildAcceptanceComposition composition):IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)=>app=>
    {
        app.Use(async(context,following)=>
        {
            string path=context.Request.Path.Value??"";
            if(composition.Observe() is not{AutomatedPrerequisitesVerified:true,IsolationVerified:true})
            {context.Response.StatusCode=StatusCodes.Status503ServiceUnavailable;return;}
            if(path.StartsWith("/api/admin/",StringComparison.OrdinalIgnoreCase) && (path.EndsWith("/reset",StringComparison.OrdinalIgnoreCase)
                || path.EndsWith("/restore",StringComparison.OrdinalIgnoreCase) || path.EndsWith("/recreate",StringComparison.OrdinalIgnoreCase)))
            {context.Response.StatusCode=StatusCodes.Status403Forbidden;await context.Response.WriteAsync("isolated acceptance forbids import, restore and reset");return;}
            context.Response.Headers["Rimlink-Runtime-Mode"]=GuildStandFiles.Mode;
            context.Response.Headers["Rimlink-Stand-Id"]=composition.Bootstrap.Stand.StandId;
            await following(context);
        });next(app);
    };
}
