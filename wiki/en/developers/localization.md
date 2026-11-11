---
pageId: developers/localization
contentRevision: 9
---

# Localization

Applies to: Rimlink 0.10.1 · RimWorld 1.7 · protocol 1.

## Usage {#usage}

Every mod-facing string is a keyed string under mod/Languages/English and Russian. Add both versions in the same task with matching keys/placeholders; do not translate technical identifiers. Inspect actual dialogs, label lengths and grammar in context.

The current-source Rimlink.Flow.SaveMissingMods key takes one parameter, {0}: inactive mod names from the player's own save. Both RU and EN state that the map was not loaded and ask the player to enable the mods before reconnecting. Do not replace this refusal with a warning that still loads an incompatible map.

The wiki uses matching pageIds and shared explicit anchors in RU/EN. Both articles carry the same contentRevision. A semantic change requires updating both versions and the manifest, not replacing English with a link to Russian. Navigation labels are translated too. Search runs locally and normalizes case and ё/е.

Run test:checks and check:content after translating; manually compare meaning, warnings and numeric limits. Matching metadata does not prove translation quality.

## Verify translation as behavior {#translation-work}

Choose a key and parameter meaning before adding matching English/Russian placeholders. Verify plurals, substitutions, button length and warnings before dangerous actions. Never translate technical IDs, configuration keys or paths.

Translate the full wiki scenario, not just its title. Compare numbers, prerequisites, actors and failure consequences. Increment shared contentRevision and update coverage questions/sources. Shared anchors preserve language switching with hashes. Structural PASS cannot establish semantic translation quality.

## Limits and safety {#limits}

Do not hard-code C# UI text instead of localization keys. Do not add external search/analytics for localization. Examples must not contain real tokens, personal saves or private logs.


Unreleased guild delivery adds paired Refusal.Guild.Client, Refusal.Guild.Unavailable, Refusal.Guild.Delivery, and Guild.DeliveryComplete/Guild.DeliveryHold keys. ParcelGuildResources has English and Russian letter labels; an unknown guild receipt is not presented as successful ordinary delivery.

## See also {#related}

- [Contributing and wiki maintenance](./contributing.md)
- [Testing and acceptance](./testing.md)
- [Glossary](../glossary.md)

RU/EN labels were added for the pre-Start view’s lack of simulation and return to the home colony. RecoveryRequired remains a separate state; loading the UI does not establish native HOME acceptance.
