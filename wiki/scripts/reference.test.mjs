import test from 'node:test';
import assert from 'node:assert/strict';
import {optionSurface,routeSurface,infrastructureRoutes,adminCommands} from './reference-lib.mjs';
test('infrastructure routes survive extraction from Program into the shared composition',()=>assert.deepEqual(infrastructureRoutes('app.MapHealthChecks("/health"); app.MapMetrics("/metrics");'),[{method:'GET',path:'/health',scope:'public'},{method:'GET',path:'/metrics',scope:'public'}]));
test('options include arrays nullable and implicit defaults, not derived getters',()=>assert.deepEqual(optionSurface('public sealed class AuthOptions { public const string Section = "Auth"; public bool AllowPassword { get; set; } public string[] Names { get; set; } = []; public string? Token { get; set; } public int Derived => 1; }'),[{key:'Auth:AllowPassword',type:'bool',default:'false'},{key:'Auth:Names',type:'string[]',default:'[]'},{key:'Auth:Token',type:'string?',default:'null'}]));
test('routes include prefixes scope and chained groups',()=>assert.deepEqual(routeSurface('var group = app.MapGroup("/api/admin"); group.MapGet("/a", Handler).RequireScope(ApiScope.Read); app.MapGroup("/api/admin").RequireAdmin().MapGet("/profiles/{id:long}", Handler).RequireScope(ApiScope.Moderate);'),[{method:'GET',path:'/api/admin/a',scope:'read'},{method:'GET',path:'/api/admin/profiles/{id:long}',scope:'moderate'}]));
test('admin inventory includes nested commands and influence dispatcher',()=>assert.deepEqual(adminCommands(`return command switch {
 "raids" => a[0] switch {
     "list" => 1,
 },
 }; private static async Task<string> RunInfluence() { int expected = command switch { "influence-disable" => 6 }; }`),['influence-disable','raids']));
