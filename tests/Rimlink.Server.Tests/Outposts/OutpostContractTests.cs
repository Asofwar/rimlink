using Google.Protobuf;
using Google.Protobuf.Reflection;
using Rimlink.Protocol;
using Rimlink.Protocol.V1;
using Xunit;

namespace Rimlink.Server.Tests.Outposts;

public sealed class OutpostContractTests
{
    [Fact]
    public void Existing_proto_tags_and_enum_values_are_unchanged()
    {
        FileDescriptor[] files = [AuthReflection.Descriptor, EconomyReflection.Descriptor, SavesReflection.Descriptor, EventsReflection.Descriptor, CommonReflection.Descriptor];
        var messages = files.SelectMany(f => f.MessageTypes).ToDictionary(m => m.Name);
        foreach (string row in LegacyFields.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = row.Split('|');
            foreach (string entry in parts[1].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                var expected = entry.Split(':');
                var field = messages[parts[0]].FindFieldByName(expected[0]);
                Assert.NotNull(field);
                Assert.Equal(int.Parse(expected[1]), field.FieldNumber);
                string actualType = field.FieldType switch {
                    FieldType.Message => field.MessageType.Name,
                    FieldType.Enum => field.EnumType.Name,
                    _ => field.FieldType.ToString().ToLowerInvariant()
                };
                Assert.Equal(expected[2], actualType);
                Assert.Equal(expected[3] == "r", field.IsRepeated);
            }
        }
        var enums = files.SelectMany(f => f.EnumTypes).ToDictionary(e => e.Name);
        foreach (string row in LegacyEnums.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = row.Split('|');
            foreach (string entry in parts[1].Split(','))
            {
                var expected = entry.Split(':');
                Assert.Equal(int.Parse(expected[1]), enums[parts[0]].FindValueByName(expected[0]).Number);
            }
        }
        Assert.Equal(1, ProtocolVersion.Current);
    }

    [Fact]
    public void Outposts_service_preserves_eight_operations_and_appends_two_ordinary_return_operations()
    {
        var service = Assert.Single(OutpostsReflection.Descriptor.Services);
        Assert.Equal("Outposts", service.Name);
        Assert.Equal(new[] {
            "State:OutpostStateQuery:OutpostStateReply", "Manifest:OutpostManifestReadRequest:OutpostManifestPage",
            "Prepare:OutpostPrepareRequest:OutpostOperationReply",
            "Commit:OutpostProofRequest:OutpostOperationReply", "Cancel:OutpostCancelRequest:OutpostOperationReply",
            "Mutate:OutpostMutationRequest:OutpostOperationReply", "Operation:OutpostOperationQuery:OutpostOperationReply",
            "BeginDelivery:OutpostDeliveryRequest:OutpostDeliveryReply",
            "OrdinaryOperation:OrdinaryOperationQuery:OrdinaryOperationReply",
            "BeginOrdinaryReturn:OrdinaryCancelRequest:OrdinaryOperationReply"
        }, service.Methods.Select(m => $"{m.Name}:{m.InputType.Name}:{m.OutputType.Name}"));
        Assert.DoesNotContain(OutpostsReflection.Descriptor.Dependencies, d => d.Name == "events.proto");
    }

    [Fact]
    public void Legacy_pawn_and_parcel_have_absent_nullable_extensions()
    {
        var pawn = PawnBlob.Parser.ParseFrom(new byte[] { 10, 5, 72, 117, 109, 97, 110 });
        var parcel = Parcel.Parser.ParseFrom(new byte[] { 8, 7 });
        Assert.Equal("Human", pawn.KindDef);
        Assert.Null(pawn.Registry);
        Assert.Null(parcel.OutpostPayload);
        Assert.Null(OutpostPayloadCompatibility.Refusal(parcel, 0));
        Assert.Null(OutpostPayloadCompatibility.Refusal(parcel, 99)); // capability is irrelevant for genuine legacy payloads
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void Tagged_payload_requires_exact_supported_capability(uint capability)
    {
        Assert.Equal(ErrorCode.OutpostClientRequired, OutpostPayloadCompatibility.Refusal(Resource(), capability));
        var tagged = new Parcel { Pawns = new PawnList { Pawns = { new PawnBlob { Registry = Registry() } } } };
        Assert.Equal(ErrorCode.OutpostClientRequired, OutpostPayloadCompatibility.Refusal(tagged, capability));
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void Unknown_payload_version_is_not_legacy_empty(uint version)
    {
        var parcel = Resource(); parcel.OutpostPayload.SchemaVersion = version;
        Assert.Equal(ErrorCode.OutpostPayloadUnsupported, OutpostPayloadCompatibility.Refusal(parcel, 1));
    }

    [Fact]
    public void Unknown_payload_kind_or_fields_fail_closed()
    {
        var parcel = Resource(); parcel.OutpostPayload.Kind = (OutpostPayloadKind)789;
        Assert.Equal(ErrorCode.OutpostPayloadUnsupported, OutpostPayloadCompatibility.Refusal(parcel, 1));
        parcel = Resource();
        parcel.OutpostPayload = OutpostParcelPayload.Parser.ParseFrom(parcel.OutpostPayload.ToByteArray().Concat(new byte[] { 0x98, 0x06, 1 }).ToArray());
        Assert.Equal(ErrorCode.OutpostPayloadUnsupported, OutpostPayloadCompatibility.Refusal(parcel, 1));
    }

    [Fact]
    public void Versioned_resource_and_tagged_pawn_roundtrip_without_xml_fabrication()
    {
        var parcel = Parcel.Parser.ParseFrom(Resource().ToByteArray());
        Assert.Null(parcel.Items);
        Assert.Equal("WoodLog", Assert.Single(parcel.OutpostPayload.Resources).DefName);
        Assert.Null(OutpostPayloadCompatibility.Refusal(parcel, 1));
        var pawn = new Parcel { Pawns = new PawnList { Pawns = { new PawnBlob { Registry = Registry() } } } };
        Assert.Null(OutpostPayloadCompatibility.Refusal(Parcel.Parser.ParseFrom(pawn.ToByteArray()), 1));
    }

    [Theory]
    [InlineData("Gold", 1)]
    [InlineData("WoodLog", 0)]
    [InlineData("Steel", -1)]
    public void Invalid_resource_is_not_a_supported_payload(string def, int count)
    {
        var parcel = Resource(); parcel.OutpostPayload.Resources[0].DefName = def; parcel.OutpostPayload.Resources[0].Count = count;
        Assert.Equal(ErrorCode.OutpostPayloadInvalid, OutpostPayloadCompatibility.Refusal(parcel, 1));
    }

    [Fact]
    public void Registry_unknown_schema_and_missing_identity_fail_closed()
    {
        var registry = Registry();
        var parcel = new Parcel { Pawns = new PawnList { Pawns = { new PawnBlob { Registry = registry } } } };
        registry.SchemaVersion = 2;
        Assert.Equal(ErrorCode.OutpostPayloadUnsupported, OutpostPayloadCompatibility.Refusal(parcel, 1));
        registry.SchemaVersion = 1; registry.PawnCustodyId = "";
        Assert.Equal(ErrorCode.OutpostPayloadInvalid, OutpostPayloadCompatibility.Refusal(parcel, 1));
    }

    [Fact]
    public void Auth_and_save_headers_preserve_explicit_outpost_versions_and_boundary()
    {
        Assert.Equal(0u, new LoginReply().OutpostVersion); // defining support is not advertising feature readiness
        var login = LoginRequest.Parser.ParseFrom(new LoginRequest { OutpostVersion = 1 }.ToByteArray());
        Assert.Equal(1u, login.OutpostVersion);
        Assert.Equal(1u, RegisterRequest.Parser.ParseFrom(new RegisterRequest { OutpostVersion = 1 }.ToByteArray()).OutpostVersion);
        var header = SaveHeader.Parser.ParseFrom(new SaveHeader { OutpostVersion = 1, OutpostRestoreBoundary = Id, RawSha256 = Hash }.ToByteArray());
        Assert.Equal(1u, header.OutpostVersion); Assert.Equal(Id, header.OutpostRestoreBoundary); Assert.Equal(Hash, header.RawSha256);
        Assert.Equal(1u, SaveInfo.Parser.ParseFrom(new SaveInfo { OutpostVersion = 1 }.ToByteArray()).OutpostVersion);
        Assert.Null(new SaveHeader().OutpostMetadata);
    }

    [Fact]
    public void Immutable_proofs_and_worker_capture_survive_wire_roundtrip()
    {
        var request = new OutpostPrepareRequest {
            Scope = Scope(), ClientRequestId = Id, Kind = OutpostPrepareKind.Found, ExpectedRevision = 5,
            ManifestId = Id, ManifestDigest = Hash, OutpostId = Id, Profile = OutpostProfile.Wood,
            Tile = 17, SurfaceId = "main", PreRemoval = new OutpostSnapshotProof { Scope = Scope(), SnapshotId = Id, RawSha256 = Hash },
            IntentDigest = Hash,
            Workers = { new OutpostWorkerCapture { PawnCustodyId = Id, AssignmentId = Id, Pawn = new PawnBlob { Registry = Registry(), ScribeXmlZstd = Hash }, BlobSha256 = Hash,
                Plants = 10, Mining = 20, Consciousness = 0.75f, Manipulation = 1f, Moving = 0.5f, PlantsCapable = true, MiningCapable = false } }
        };
        var copy = OutpostPrepareRequest.Parser.ParseFrom(request.ToByteArray());
        Assert.Equal(request, copy); Assert.False(copy.Workers[0].MiningCapable); Assert.Equal(0.75f, copy.Workers[0].Consciousness);
        Assert.Equal(Hash, copy.PreRemoval.RawSha256); Assert.Equal(5, copy.ExpectedRevision);
        var proof = new SnapshotProof(Id, Hash, new OutpostScope("world", 7, 2, 3, Id));
        Assert.Equal(Hash, proof.RawSha256);
        Assert.Equal(Id, proof.Scope.RestoreBoundary);
    }

    [Fact]
    public void Delivery_authorization_binds_original_parcel_digest_snapshot_and_scope()
    {
        var request = new OutpostDeliveryRequest { Scope = Scope(), ClientRequestId = Id, ParcelId = 41, PayloadDigest = Hash,
            CanonicalSnapshot = new OutpostSnapshotProof { Scope = Scope(), SnapshotId = Id, RawSha256 = Hash } };
        Assert.Equal(request, OutpostDeliveryRequest.Parser.ParseFrom(request.ToByteArray()));
        var reply = new OutpostDeliveryReply { AttemptId = Id, Authorization = request, AuthoritativeRevision = 8 };
        Assert.Equal(request, OutpostDeliveryReply.Parser.ParseFrom(reply.ToByteArray()).Authorization);
        Assert.Equal(112, ServerEvent.Descriptor.FindFieldByName("outpost_changed").FieldNumber);
    }

    [Fact]
    public void Unknown_nested_resource_or_registry_fields_are_not_ignored()
    {
        var parcel = Resource();
        parcel.OutpostPayload.Resources[0] = ResourceParcel.Parser.ParseFrom(
            parcel.OutpostPayload.Resources[0].ToByteArray().Concat(new byte[] { 0x98, 0x06, 1 }).ToArray());
        Assert.Equal(ErrorCode.OutpostPayloadUnsupported, OutpostPayloadCompatibility.Refusal(parcel, 1));
        var registry = PawnRegistryEnvelope.Parser.ParseFrom(Registry().ToByteArray().Concat(new byte[] { 0x98, 0x06, 1 }).ToArray());
        parcel = new Parcel { Pawns = new PawnList { Pawns = { new PawnBlob { Registry = registry } } } };
        Assert.Equal(ErrorCode.OutpostPayloadUnsupported, OutpostPayloadCompatibility.Refusal(parcel, 1));
    }

    [Fact]
    public void Outpost_kind_without_envelope_cannot_be_treated_as_legacy_empty()
    {
        var parcel = new Parcel { Kind = ParcelKind.ParcelOutpost };
        Assert.Equal(ErrorCode.OutpostClientRequired, OutpostPayloadCompatibility.Refusal(parcel, 0));
        Assert.Equal(ErrorCode.OutpostPayloadInvalid, OutpostPayloadCompatibility.Refusal(parcel, 1));
        parcel.Kind = (ParcelKind)999;
        Assert.Equal(ErrorCode.OutpostPayloadUnsupported, OutpostPayloadCompatibility.Refusal(parcel, 1));
    }

    [Fact]
    public void Missing_provenance_and_kind_body_mismatch_refuse_entire_batch()
    {
        var parcel = Resource(); parcel.OutpostPayload.PayloadDigest = ByteString.Empty;
        Assert.Equal(ErrorCode.OutpostPayloadInvalid, OutpostPayloadCompatibility.Refusal(parcel, 1));
        parcel = Resource(); parcel.OutpostPayload.Scope = null;
        Assert.Equal(ErrorCode.OutpostPayloadInvalid, OutpostPayloadCompatibility.Refusal(parcel, 1));
        parcel = Resource(); parcel.OutpostPayload.Resources.Clear();
        Assert.Equal(ErrorCode.OutpostPayloadInvalid, OutpostPayloadCompatibility.Refusal(parcel, 1));
        parcel = Resource(); parcel.OutpostPayload.Kind = OutpostPayloadKind.Workers;
        Assert.Equal(ErrorCode.OutpostPayloadInvalid, OutpostPayloadCompatibility.Refusal(parcel, 1));
        parcel = Resource(); parcel.Items = new ItemList { Items = { new ItemBlob { DefName = "Steel", Count = 1 } } };
        Assert.Equal(ErrorCode.OutpostPayloadInvalid, OutpostPayloadCompatibility.Refusal(parcel, 1));
    }

    [Fact]
    public void Return_batch_requires_exact_original_registry_pairs_without_duplicates()
    {
        var parcel = Resource(); parcel.OutpostPayload.Kind = OutpostPayloadKind.ReturnBatch;
        parcel.Pawns = new PawnList { Pawns = { new PawnBlob { Registry = Registry() } } };
        parcel.OutpostPayload.PawnCustodyIds.Add(Id); parcel.OutpostPayload.AssignmentIds.Add(Id);
        Assert.Null(OutpostPayloadCompatibility.Refusal(parcel, 1));
        parcel.OutpostPayload.AssignmentIds[0] = "22222222222222222222222222222222";
        Assert.Equal(ErrorCode.OutpostPayloadInvalid, OutpostPayloadCompatibility.Refusal(parcel, 1));
        parcel.OutpostPayload.AssignmentIds[0] = Id;
        parcel.Pawns.Pawns.Add(parcel.Pawns.Pawns[0].Clone());
        parcel.OutpostPayload.PawnCustodyIds.Add(Id); parcel.OutpostPayload.AssignmentIds.Add(Id);
        Assert.Equal(ErrorCode.OutpostPayloadInvalid, OutpostPayloadCompatibility.Refusal(parcel, 1));
    }

    private const string Id = "11111111111111111111111111111111";
    private static readonly ByteString Hash = ByteString.CopyFrom(new byte[32]);
    private static OutpostScopeMessage Scope() => new() { WorldInstanceId = "world", OwnerPlayerId = 7, SaveGeneration = 2, SaveWriteEpoch = 3, RestoreBoundary = Id };
    private static PawnRegistryEnvelope Registry() => new() { SchemaVersion = 1, PawnCustodyId = Id, AssignmentId = Id, Scope = Scope(), PawnReferenceToken = "pawn-reference" };
    private static Parcel Resource() => new() { Kind = ParcelKind.ParcelOutpost, OutpostPayload = new OutpostParcelPayload {
        SchemaVersion = 1, Kind = OutpostPayloadKind.Resources, Scope = Scope(), OutpostId = Id, BatchOperationId = Id, PayloadDigest = Hash,
        ManifestId = Id, ManifestDigest = Hash, ValuationRevision = 1,
        Resources = { new ResourceParcel { SchemaVersion = 1, DefName = "WoodLog", Count = 100, ImportValueX100 = 12000 } }
    } };

    // The shipped 0.8.1 wire contract, not a snapshot generated by the implementation under test.
    private const string LegacyFields = """
        PasswordCredential|login:1:string:s,password:2:string:s
        LoginRequest|protocol_version:1:int32:s,steam_ticket:2:bytes:s,password:3:PasswordCredential:s,server_password:4:string:s,mods:5:ModRef:r,mod_settings:6:ModSettingsHash:r,recovery_version:7:uint32:s
        RegisterRequest|login:1:string:s,password:2:string:s,player_name:3:string:s,mods:4:ModRef:r,mod_settings:5:ModSettingsHash:r,recovery_version:6:uint32:s,server_password:7:string:s
        AtHomeRequest|at_home:1:bool:s,coop_capable:2:bool:s
        LoginReply|session_token:1:string:s,player:2:PlayerRef:s,role:3:Role:s,world_exists:4:bool:s,has_save:5:bool:s,has_settlement:6:bool:s,modpack_diff:7:ModpackDiff:s,online:8:PlayerRef:r,pending_live_ends:9:LiveSession:r,dev_mode_allowed:10:bool:s,colony_restore:11:ColonyRestore:s,save_generation:12:int64:s,save_write_epoch:13:int64:s,recovery_version:14:uint32:s
        ColonyRestore|restored:1:bool:s,name:2:string:s,tile:3:int32:s,archived_at:4:Timestamp:s,reason:5:string:s,influence_notice_key:6:string:s
        SkillBrief|skill_def:1:string:s,level:2:int32:s
        ItemBlob|def_name:1:string:s,stuff_def:2:string:s,count:3:int32:s,quality:4:int32:s,hit_points:5:int32:s,max_hit_points:6:int32:s,market_value_x100:7:int64:s,label:8:string:s,scribe_xml_zstd:9:bytes:s,perishable:10:bool:s
        PawnBlob|kind_def:1:string:s,name:2:string:s,humanlike:3:bool:s,age_years:4:int32:s,label:5:string:s,skills:6:SkillBrief:r,scribe_xml_zstd:7:bytes:s,market_value_x100:8:int64:s
        ItemList|items:1:ItemBlob:r
        PawnList|pawns:1:PawnBlob:r
        Parcel|id:1:int64:s,from:2:PlayerRef:s,kind:3:ParcelKind:s,items:4:ItemList:s,pawns:5:PawnList:s,silver:6:int64:s,created_at:7:Timestamp:s,redeliver:8:bool:s,delivered_tick:9:int64:s,size_bytes:10:int32:s,awaiting_decision:11:bool:s
        ParcelList|parcels:1:Parcel:r
        Lot|id:1:int64:s,seller:2:PlayerRef:s,items:3:ItemList:s,pawn:4:PawnBlob:s,price:5:int64:s,created_at:6:Timestamp:s,expires_at:7:Timestamp:s,status:8:LotStatus:s
        LotList|lots:1:Lot:r,total:2:int32:s
        WalletState|balance:1:int64:s,server_wealth:2:int64:s
        EconomyRules|forbidden_defs:1:string:r,storage_fee_percent_per_day:2:double:s,storage_free_days:3:double:s
        WalletEntryMsg|id:1:int64:s,delta:2:int64:s,kind:3:string:s,ref_id:4:int64:s,at:5:Timestamp:s
        WalletHistory|entries:1:WalletEntryMsg:r
        DepositRequest|client_request_id:1:string:s,silver:2:int64:s,game_tick:3:int64:s
        WithdrawRequest|client_request_id:1:string:s,silver:2:int64:s
        ParcelRef|parcel_id:1:int64:s
        ParcelAnswer|parcel_id:1:int64:s,accept:2:bool:s
        DeliverRequest|parcel_id:1:int64:s,game_tick:2:int64:s,snapshot_id:3:string:s,save_generation:4:int64:s,save_write_epoch:5:int64:s
        MarketQuery|seller_player_id:1:int64:s,text:2:string:s,page:3:int32:s
        PostRequest|client_request_id:1:string:s,items:2:ItemList:s,pawn:3:PawnBlob:s,price:4:int64:s,game_tick:5:int64:s
        LotRef|lot_id:1:int64:s
        TransferRequest|client_request_id:1:string:s,to_player_id:2:int64:s,items:3:ItemList:s,pawns:4:PawnList:s,silver:5:int64:s,game_tick:6:int64:s,from_caravan:7:bool:s
        ParcelArrived|parcel:1:Parcel:s
        LotSold|lot_id:1:int64:s,buyer:2:PlayerRef:s,price:3:int64:s,proceeds:4:int64:s,storage_fee:5:int64:s
        LotClosed|lot_id:1:int64:s,reason:2:string:s,storage_fee:3:int64:s
        WalletChanged|balance:1:int64:s,delta:2:int64:s,kind:3:string:s,ref_id:4:int64:s
        GiftRefused|parcel_id:1:int64:s,by:2:PlayerRef:s,returned:3:string:s,expired:4:bool:s
        SaveChunk|header:1:SaveHeader:s,data:2:bytes:s
        SaveHeader|game_tick:1:int64:s,world_seed:2:string:s,faction_name:3:string:s,total_size:4:int64:s,sha256:5:bytes:s,wealth_total:6:int64:s,wealth_items:7:int64:s,dev_mode:8:bool:s,save_generation:9:int64:s,snapshot_id:10:string:s,save_write_epoch:11:int64:s,recovery_version:12:uint32:s
        SaveInfo|version:1:int32:s,size:2:int64:s,created_at:3:Timestamp:s,is_current:4:bool:s,game_tick:5:int64:s,snapshot_id:6:string:s,save_write_epoch:7:int64:s,recovery_version:8:uint32:s
        SaveVersions|versions:1:SaveInfo:r
        SaveRolledBack|version:1:int32:s,save_write_epoch:2:int64:s
        ServerEvent|sequence:1:int64:s,at:2:Timestamp:s,player_joined:10:PlayerJoined:s,player_left:11:PlayerLeft:s,session_replaced:12:SessionReplaced:s,chat:20:ChatMessageEvent:s,world_object_added:30:WorldObjectAdded:s,world_object_removed:31:WorldObjectRemoved:s,world_object_updated:32:WorldObjectUpdated:s,notice:40:Notice:s,shutdown:41:ServerShutdown:s,save_rolled_back:42:SaveRolledBack:s,parcel_arrived:50:ParcelArrived:s,lot_sold:51:LotSold:s,lot_closed:52:LotClosed:s,wallet_changed:53:WalletChanged:s,raid_queued:60:RaidQueued:s,raid_landed:61:RaidLanded:s,raid_resolved:62:RaidResolved:s,raid_expired:63:RaidExpired:s,item_sold:64:ItemSold:s,visit_started:65:VisitStarted:s,player_flags_changed:66:PlayerFlagsChanged:s,live_invite:70:LiveInvite:s,live_started:71:LiveStarted:s,live_ended:72:LiveEnded:s,live_fallback:73:LiveFallback:s,live_peer_lost:74:LivePeerLost:s,live_peer_back:75:LivePeerBack:s,gift_refused:76:GiftRefused:s,trade_updated:77:TradeUpdated:s,ping:78:PingEvent:s,incident_queued:80:IncidentQueued:s,incident_resolved:81:IncidentResolved:s,procurement_changed:90:ProcurementChanged:s,auction_updated:110:AuctionUpdated:s,community_changed:100:CommunityChanged:s,influence_changed:111:InfluenceChanged:s
        PlayerJoined|player:1:PlayerRef:s
        PlayerLeft|player:1:PlayerRef:s
        SessionReplaced|
        Notice|text:1:string:s
        ServerShutdown|in_seconds:1:int32:s,reason:2:string:s
        Empty|
        ErrorDetail|code:1:ErrorCode:s,message:2:string:s,key:3:string:s,args:4:string:r
        PlayerRef|player_id:1:int64:s,name:2:string:s,faction_name:3:string:s,color_argb:4:uint32:s
        ModRef|package_id:1:string:s,version:2:string:s,position:3:int32:s,files_hash:4:string:s,has_defs:5:bool:s,has_patches:6:bool:s,has_assemblies:7:bool:s
        ModSettingsHash|package_id:1:string:s,handle:2:string:s,hash:3:string:s
        """;
    private const string LegacyEnums = """
        Role|PLAYER:0,MODERATOR:1,ADMIN:2
        ParcelKind|PARCEL_GIFT:0,PARCEL_PURCHASE:1,PARCEL_LOT_RETURN:2,PARCEL_WITHDRAW:3,PARCEL_ADMIN:4,PARCEL_LOOT:5,PARCEL_RAID_RETURN:6,PARCEL_CAPTIVE:7,PARCEL_GIFT_RETURN:8,PARCEL_TRADE:9,PARCEL_TRADE_RETURN:10
        LotStatus|LOT_OPEN:0,LOT_SOLD:1,LOT_WITHDRAWN:2,LOT_EXPIRED:3
        ErrorCode|UNSPECIFIED:0,WORLD_NOT_CREATED:1,TILE_TAKEN:2,SETTLEMENT_LIMIT:3,MODPACK_MISMATCH:4,BANNED:5,SERVER_FULL:6,WRONG_PASSWORD:7,PROTOCOL_VERSION:8,NAME_INVALID:9,SAVE_INVALID:10,MUTED:11,WALLET_INSUFFICIENT:12,LOT_GONE:13,PARCEL_TOO_BIG:14,DEF_NOT_IN_MODPACK:15,SAVE_STALE:16,RECIPIENT_UNKNOWN:17,ECONOMY_FROZEN:18,SELF_TRADE:19,RAID_COOLDOWN:20,TARGET_PROTECTED:21,PEACEFUL_MODE:22,RAID_ACTIVE:23,RAID_GONE:24,LOOT_OVER_CAP:25,VISITS_CLOSED:26,ITEM_SOLD:27,HOST_OFFLINE:28,LIVE_ACTIVE:29,LIVE_GONE:30,LIVE_DISABLED:31,BLOCKED:32,TRADES_CLOSED:33,TRADE_GONE:34,TRADE_CHANGED:35,TRADE_LIMIT:36,MODPACK_CONFIGS_INVALID:37,PLAYER_FROZEN:38,UNDER_THREAT:39,IN_DEBT:40,DEF_FORBIDDEN:41,INCIDENT_QUEUE_FULL:42,STEAM_TICKET_REJECTED:43,PROCUREMENT_INVALID:44,PROCUREMENT_GONE:45,PROCUREMENT_ACCESS:46,PROCUREMENT_LIMIT:47,PROCUREMENT_INTENT:48,PROCUREMENT_GOODS:49,ALLIANCE_REFUSED:50,COMMUNITY_LIMIT:51,COMMUNITY_GONE:52,TREASURY_LIMIT:53,COMMUNITY_INTENT:54,ALLIED_TARGET:55,AUCTION_GONE:56,AUCTION_BID_TOO_LOW:57,AUCTION_HAS_BIDS:58,AUCTION_INTENT_MISMATCH:59,SAVE_GENERATION_MISMATCH:60,INFLUENCE_CONFLICT:61,INFLUENCE_INELIGIBLE:62,INFLUENCE_CAPACITY:63,INFLUENCE_INVALID:64,INFLUENCE_INTENT:65,INFLUENCE_UNAVAILABLE:66,SAVE_WRITE_EPOCH_MISMATCH:67,RECOVERY_CLIENT_REQUIRED:68,REQUEST_INTENT_MISMATCH:69,RECOVERY_REQUIRED:70
        """;
}
