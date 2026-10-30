# Publishing Rimlink on the Steam Workshop

The mod is uploaded from RimWorld itself, through the Steam client that is already running. There is no SteamCMD and nothing
to log in to; the item belongs to the Steam account in that client.

What goes where:

| What | Source | How it reaches Steam |
|---|---|---|
| Title, tags, files | `About/About.xml`, the mod folder | every upload from the game |
| Preview image | `mod/About/Preview.png`, under 1 MB | every upload from the game |
| Page text | `docs/workshop/description.bbcode`, English and Russian, under 8000 bytes | pasted on the item page; the game sets a description only when it creates the item, from `About.xml` |
| Gallery | `docs/images/gallery-*.jpg` | added on the item page |
| Required items | Harmony | added on the item page |

## Upload

1. Close the game. Unpack the release's `rimlink-client-<version>.zip` over `<RimWorld>/Mods/`. Upload the release's own
   files, not a fresh build: the server's modpack check hashes `Assemblies/`, `Defs/` and `Patches/`, a build from another
   folder has other hashes, and a server whose modpack came from one copy would refuse players of the other. `About/` is not
   hashed, so a new preview or description can be copied over the zip's.
2. Start RimWorld with Steam running. Turn on **Development mode** in Options and stay in the main menu: on most servers
   Rimlink turns dev mode off while you are connected.
3. Open **Mods**, select Rimlink, **Advanced...** → **Upload to Steam Workshop** (**Update on Steam Workshop** once it exists).
4. Confirm the Workshop terms, leave **Tag as translation** off, and answer **Yes** to the question about having made the
   content yourself (the button waits six seconds).

The game opens the item page when it is done.

## After the first upload

1. Accept the Workshop legal agreement if the page asks for it: the item stays hidden until then.
2. **Edit title & description**: paste `description.bbcode`.
3. **Add/edit images & videos**: add `docs/images/gallery-*.jpg`.
4. **Add/Edit Required Items**: add Harmony (Workshop id 2009463077).
5. Set the visibility to Public.
6. Copy `About/PublishedFileId.txt` from `<RimWorld>/Mods/Rimlink` to `mod/About/` and commit it. Without it the next
   `Build-ClientMod.ps1 -InstallTo` wipes the id and the next upload creates a second item.
7. Put the Workshop link into `README.md` and `docs/player-guide.md`.

An update through the game replaces the title, preview, tags and files; the page text, gallery and required items stay as they
are. Its change note is an automatic line, which you can replace on the page's **Change Notes** tab.
