# Frequently Asked Questions

- [I am a user](#i-am-a-user)
    - [Q: I don't want to install the *XYZ* mod, but the server keeps trying to download it. How do I stop that?](#q-i-dont-want-to-install-the-xyz-mod-but-the-server-keeps-trying-to-download-it-how-do-i-stop-that)
    - [Q: I want to download all of the server mods in addition to client mods so that I can keep playing whenever our server is offline.](#q-i-want-to-download-all-of-the-server-mods-in-addition-to-client-mods-so-that-i-can-keep-playing-whenever-our-server-is-offline)
    - [Q: I installed ModSync and I'm getting stuck on the "Acquiring Bundles" screen. Why does it take so long?](#q-i-installed-modsync-and-im-getting-stuck-on-the-acquiring-bundles-screen-why-does-it-take-so-long)
    - [Q: I'm getting an error when using ModSync, what do I do?](#q-im-getting-an-error-when-using-modsync-what-do-i-do)
- [I am an admin](#i-am-an-admin)
    - [Q: How do I make sure a specific file/folder does not sync to clients?](#q-how-do-i-make-sure-a-specific-filefolder-does-not-sync-to-clients)
    - [Q: How do I make sure a specific file/folder ***does*** sync to clients?](#q-how-do-i-make-sure-a-specific-filefolder-does-sync-to-clients)
    - [Q: One of my clients changed a file and ModSync didn't detect the update. What gives?](#q-one-of-my-clients-changed-a-file-and-modsync-didnt-detect-the-update-what-gives)
    - [Q: I'm using the FIKA Dedicated plugin to host raids on my server. Can I use ModSync with it?](#q-im-using-the-fika-dedicated-plugin-to-host-raids-on-my-server-can-i-use-modsync-with-it)
    - [Q: I'm getting an error when using ModSync, what do I do?](#q-im-getting-an-error-when-using-modsync-what-do-i-do-1)

# I am a user

### Q: I don't want to install the *XYZ* mod, but the server keeps trying to download it. How do I stop that?
A: No problem! Just add the file/folder path of the mod you don't want downloaded to your [local exclusions file](Configuration#exclusionsjsonc)

### Q: I want to download all of the server mods in addition to client mods so that I can keep playing whenever our server is offline.
A: Check the [BepInEx configuration](Configuration#bepinex-configuration-manager). If `user/mods` is listed there, just enable it and restart your game to download the server files.

### Q: I installed ModSync and I'm getting stuck on the "Bundle Loading" screen. Why does it take so long?
A: ModSync doesn't touch the bundle loading process. In fact, I've taken specific steps to avoid messing with how bundles are downloaded.
If you are seeing this screen you are dealing with an SPT issue, not a ModSync issue. Strategies for dealing with it can be found in the
[FIKA discord server](https://discord.com/channels/1202292159366037545/1234332919443488799/1235518309882007552)

### Q: I'm getting an error when using ModSync, what do I do?
A: Go ahead and post a message in the [ModSync thread on the FIKA discord server](https://discord.com/channels/1202292159366037545/1249759503516434473) **with your `BepInEx/LogOutput.log` and `ModSync_Data/ModSync.log`**, and either myself or another
member of the community will do our best to help you.

# I am an admin

### Q: How do I make sure a specific file/folder does not sync to clients?
A: [Check out the documentation for configuring exclusions](Configuration#exclusions)

### Q: How do I make sure a specific file/folder ***does*** sync to clients?
A: [Check out the documentation for configuring Sync Paths](Configuration#syncpaths), specifically the [`enforced option`](Configuration#options)

### Q: One of my clients changed a file and ModSync didn't detect the update. What gives?
A: Go ahead and review the [documentation for how ModSync works](How-Sync-Works#updated-files). If you want the behavior you're describing, take a peek at [`enforced option`](Configuration#options).

### Q: I'm using the Fika headless client. Can I use ModSync with it?
A: Yes - ModSync has first-class support for headless. When a headless client connects, the server serves only the plugins listed in [`headlessIncludes`](Configuration#headlessincludes) rather than the full plugin list. This lets you deliver exactly the bot-AI and Fika DLLs the headless needs without sending GPU-side mods it can't use.

On the headless side, ModSync stages updates to `ModSync_Data/PendingUpdates` and then quits. A [BepInEx preloader patcher](How-Sync-Works#headless-clients) applies those updates on the next boot before any plugin DLLs are loaded into memory, avoiding the locked-file errors that would otherwise cause an infinite re-sync loop.

See the [`headlessIncludes` configuration docs](Configuration#headlessincludes) for setup details.

### Q: Do I need `ModSync.Updater.exe` on my headless client? Can I remove it?
A: Headless clients never run the Updater - they apply updates with the [preloader patcher](How-Sync-Works#headless-clients) instead. By default the Updater still syncs to headless as harmless dead weight. If you want a leaner headless install, add `ModSync.Updater.exe` to that install's `ModSync_Data/Exclusions.jsonc` and ModSync removes it on the next sync. The reverse holds for players and the patcher - add `BepInEx/patchers/Corter-ModSync-Prepatch.dll` to a player's `Exclusions.jsonc`. See [trimming ModSync's own components](Configuration#trimming-modsyncs-own-components-optional).

ModSync won't let you trim the *wrong* one: a player can't exclude the Updater and a headless can't exclude the patcher, since each is enforced on the side that needs it. Note the **server** must always keep **all** ModSync files (including the Updater) so it can serve and update them to clients - that requirement never changes; the trimming is per-client only.

### Q: I'm getting an error when using ModSync, what do I do?
A: Go ahead and post a message in the [ModSync thread on the FIKA discord server](https://discord.com/channels/1202292159366037545/1249759503516434473) **with your `user/logs/server-YEAR-MONTH-DAY.log`, `BepInEx/LogOutput.log`, and `ModSync_Data/ModSync.log`**,
and either myself or another member of the community will do our best to help you.