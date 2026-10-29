namespace Rimlink.Server.Grpc;

/// <summary>
/// Keys of the refusals a player sees (live UX spec §5): the client shows <c>Rimlink.Refusal.&lt;key&gt;</c> from the mod's keyed strings
/// with the refusal's arguments, in the player's language, and falls back to the English message. A value never changes once shipped.
/// </summary>
public static class RefusalKeys
{
    public const string RateLimited = "RateLimited";

    public const string NoSuchPlayer = "Common.NoSuchPlayer";

    public const string ChatEmpty = "Chat.Empty";
    public const string ChatTooLong = "Chat.TooLong";
    public const string ChatControlChars = "Chat.ControlChars";
    public const string ChatMuted = "Chat.Muted";
    public const string ChatNotOnline = "Chat.NotOnline";
    public const string PingTile = "Chat.PingTile";
    public const string PingCell = "Chat.PingCell";

    public const string LiveDisabled = "Live.Disabled";
    public const string LiveSelf = "Live.Self";
    public const string LiveNoPawns = "Live.NoPawns";
    public const string LiveTooManyPawns = "Live.TooManyPawns";
    public const string LiveHostOffline = "Live.HostOffline";
    public const string LiveOwnSession = "Live.OwnSession";
    public const string LiveHostBusy = "Live.HostBusy";
    public const string LiveOnVisit = "Live.OnVisit";
    public const string LiveInviteGone = "Live.InviteGone";
    public const string LiveOver = "Live.Over";
    public const string LiveNotOpen = "Live.NotOpen";
    public const string LiveNoVisit = "Live.NoVisit";
    public const string LiveNone = "Live.None";
    public const string LiveNoSuchSession = "Live.NoSuchSession";
    public const string LiveWrongRole = "Live.WrongRole";
    public const string LiveOnlyHostSpawns = "Live.OnlyHostSpawns";
    public const string LiveHelloRequired = "Live.HelloRequired";

    public const string VisitsClosed = "Visits.Closed";
    public const string VisitsSelf = "Visits.Self";
    public const string VisitsInLive = "Visits.InLive";
    public const string VisitsNoColony = "Visits.NoColony";

    public const string ShopSelf = "Shop.Self";
    public const string ShopCountTooLow = "Shop.CountTooLow";
    public const string ShopNoItemData = "Shop.NoItemData";
    public const string ShopSold = "Shop.Sold";
    public const string ShopOnlyLeft = "Shop.OnlyLeft";
    public const string ShopMismatch = "Shop.Mismatch";
    public const string ShopNotVisiting = "Shop.NotVisiting";

    public const string EconomyDisabled = "Economy.Disabled";
    public const string EconomyRecipientUnknown = "Economy.RecipientUnknown";
    public const string EconomyMailboxFull = "Economy.MailboxFull";
    public const string EconomyParcelUnknown = "Economy.ParcelUnknown";
    public const string EconomyLotContents = "Economy.LotContents";
    public const string EconomyPriceTooLow = "Economy.PriceTooLow";
    public const string EconomyLotLimit = "Economy.LotLimit";
    public const string EconomyLotClosed = "Economy.LotClosed";
    public const string EconomyLotUnknown = "Economy.LotUnknown";
    public const string EconomySelfBuy = "Economy.SelfBuy";
    public const string EconomyLotGone = "Economy.LotGone";
    public const string EconomySelfSend = "Economy.SelfSend";
    public const string EconomyNegativeSilver = "Economy.NegativeSilver";
    public const string EconomyNothingToSend = "Economy.NothingToSend";
    public const string EconomyWalletInsufficient = "Economy.WalletInsufficient";
    public const string EconomySilverTooLow = "Economy.SilverTooLow";
    public const string EconomyPaused = "Economy.Paused";
    public const string EconomyPlayerFrozen = "Economy.PlayerFrozen";
    public const string EconomySellerFrozen = "Economy.SellerFrozen";
    public const string EconomyInDebt = "Economy.InDebt";
    public const string EconomyUnderThreat = "Economy.UnderThreat";

    public const string RaidsPaused = "Raids.Paused";
    public const string RaidsOutcomeIndex = "Raids.OutcomeIndex";
    public const string RaidsSurvivorNoBlob = "Raids.SurvivorNoBlob";
    public const string RaidsMissingOutcomes = "Raids.MissingOutcomes";
    public const string RaidsReportBeforeLanding = "Raids.ReportBeforeLanding";
    public const string RaidsKidnapDisabled = "Raids.KidnapDisabled";
    public const string RaidsCaptiveLimit = "Raids.CaptiveLimit";
    public const string RaidsLootOverCap = "Raids.LootOverCap";
    public const string RaidsDisabled = "Raids.Disabled";
    public const string RaidsSelf = "Raids.Self";
    public const string RaidsNoPawns = "Raids.NoPawns";
    public const string RaidsTooManyPawns = "Raids.TooManyPawns";
    public const string RaidsNoSuchRaid = "Raids.NoSuchRaid";
    public const string RaidsGone = "Raids.Gone";
    public const string RaidsLiveNotAnnounced = "Raids.LiveNotAnnounced";
    public const string RaidsLiveDoesNotLand = "Raids.LiveDoesNotLand";
    public const string RaidsNotAnnounced = "Raids.NotAnnounced";
    public const string RaidsNotLanded = "Raids.NotLanded";
    public const string RaidsPeacefulSelf = "Raids.PeacefulSelf";
    public const string RaidsPeacefulTarget = "Raids.PeacefulTarget";
    public const string RaidsTargetNew = "Raids.TargetNew";
    public const string RaidsTargetPoor = "Raids.TargetPoor";
    public const string RaidsActive = "Raids.Active";
    public const string RaidsTargetCooldown = "Raids.TargetCooldown";
    public const string RaidsPairCooldown = "Raids.PairCooldown";

    public const string WorldNotCreated = "World.NotCreated";
    public const string WorldAlreadyExists = "World.AlreadyExists";
    public const string WorldEmptySeed = "World.EmptySeed";
    public const string WorldSettlementNameInvalid = "World.SettlementNameInvalid";
    public const string WorldNpcTile = "World.NpcTile";
    public const string WorldSettlementLimit = "World.SettlementLimit";
    public const string WorldTileTaken = "World.TileTaken";
    public const string WorldObjectUnknown = "World.ObjectUnknown";
    public const string WorldOnlyAdminCreates = "World.OnlyAdminCreates";
    public const string WorldNotOwned = "World.NotOwned";

    public const string SavesStale = "Saves.Stale";
    public const string SavesHeaderRequired = "Saves.HeaderRequired";
    public const string SavesNone = "Saves.None";
    public const string SavesSize = "Saves.Size";
    public const string SavesNoHash = "Saves.NoHash";
    public const string SavesExtraData = "Saves.ExtraData";
    public const string SavesWrongSize = "Saves.WrongSize";
    public const string SavesHashMismatch = "Saves.HashMismatch";
    public const string SavesUnexpectedMessage = "Saves.UnexpectedMessage";
    public const string SavesChunkTooBig = "Saves.ChunkTooBig";
    public const string SavesNoRoot = "Saves.NoRoot";
    public const string SavesWrongWorld = "Saves.WrongWorld";
    public const string SavesNoSeed = "Saves.NoSeed";
    public const string SavesNoPlayerFaction = "Saves.NoPlayerFaction";
    public const string SavesUnreadable = "Saves.Unreadable";

    public const string AuthPasswordDisabled = "Auth.PasswordDisabled";
    public const string AuthLoginInvalid = "Auth.LoginInvalid";
    public const string AuthPlayerNameInvalid = "Auth.PlayerNameInvalid";
    public const string AuthPasswordEmpty = "Auth.PasswordEmpty";
    public const string AuthLoginTaken = "Auth.LoginTaken";
    public const string AuthPlayerNameTaken = "Auth.PlayerNameTaken";
    public const string AuthInvalidCredentials = "Auth.InvalidCredentials";
    public const string AuthSteamTicketInvalid = "Auth.SteamTicketInvalid";
    public const string AuthProtocolVersion = "Auth.ProtocolVersion";
    public const string AuthWorldNotCreated = "Auth.WorldNotCreated";
    public const string AuthServerFull = "Auth.ServerFull";
    public const string AuthWrongPassword = "Auth.WrongPassword";
    public const string AuthNoCredential = "Auth.NoCredential";
    public const string AuthBannedUntil = "Auth.BannedUntil";
    public const string AuthBannedPermanently = "Auth.BannedPermanently";

    public const string ParcelTooManyItems = "Parcel.TooManyItems";
    public const string ParcelTooManyPawns = "Parcel.TooManyPawns";
    public const string ParcelTooBig = "Parcel.TooBig";
    public const string ParcelItemCount = "Parcel.ItemCount";
    public const string ParcelItemNoData = "Parcel.ItemNoData";
    public const string ParcelDefNotInModpack = "Parcel.DefNotInModpack";
    public const string ParcelDefForbidden = "Parcel.DefForbidden";
    public const string ParcelPawnNoData = "Parcel.PawnNoData";

    public const string GiftsNoDecision = "Gifts.NoDecision";
    public const string GiftsAwaiting = "Gifts.Awaiting";

    public const string SocialYouBlocked = "Social.YouBlocked";
    public const string SocialBlockedYou = "Social.BlockedYou";
    public const string SocialSelf = "Social.Self";
    public const string SocialStaff = "Social.Staff";
    public const string SocialListFull = "Social.ListFull";

    public const string TradesSelf = "Trades.Self";
    public const string TradesClosed = "Trades.Closed";
    public const string TradesLimitOwn = "Trades.LimitOwn";
    public const string TradesLimitPartner = "Trades.LimitPartner";
    public const string TradesChanged = "Trades.Changed";
    public const string TradesGone = "Trades.Gone";
    public const string TradesNegativeSilver = "Trades.NegativeSilver";
    public const string TradesNoteTooLong = "Trades.NoteTooLong";
    public const string TradesNoteControlChars = "Trades.NoteControlChars";
    public const string TradesPartnerFrozen = "Trades.PartnerFrozen";

    public const string IncidentsDisabled = "Incidents.Disabled";
    public const string IncidentsKindCooldown = "Incidents.KindCooldown";
    public const string IncidentsQueueFull = "Incidents.QueueFull";
    public const string IncidentsUnknownEntry = "Incidents.UnknownEntry";
    public const string IncidentsSelf = "Incidents.Self";
    public const string IncidentsNoPods = "Incidents.NoPods";
    public const string IncidentsLevel = "Incidents.Level";
    public const string IncidentsNoSuchEvent = "Incidents.NoSuchEvent";
    public const string IncidentsGone = "Incidents.Gone";
}
