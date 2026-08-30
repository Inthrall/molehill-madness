# Publishing to Google Play

What the pipeline does, what only a person with the console open can do, and the traps that cost a week if they are met in the wrong order.

The build half is written and lives in [`.github/workflows/release.yml`](../.github/workflows/release.yml). It is inert until four secrets exist, and it says so on every run rather than failing.

## The short version

1. Create a Google Play developer account, if there is not one.
2. Create the app, and let Play sign it.
3. Make an upload key, and put it in the repository's secrets.
4. Make a service account, give it access to this app, and put its key in the secrets.
5. Fill in the console: content rating, target audience, data safety, privacy policy, store listing.
6. Tag a release. CI builds the bundle and sends it to the internal track.

Steps 1, 2, 4 and 5 are console work and cannot be done from here. Steps 3 and 6 are commands.

## 1. The developer account

25 US dollars, once, and identity verification that takes a few days rather than a few minutes. A personal account now needs a photograph of an identity document; an organisation needs a D-U-N-S number, which is free but takes a fortnight to be issued if Inthrall does not already have one.

**Choose the account type before paying, because it cannot be changed afterwards.** It also decides the rule below, which shapes the whole schedule.

**A personal developer account created recently cannot publish to production until it has run a closed test with at least twelve testers who stayed opted in for fourteen consecutive days.** Twelve people, opted in, for two weeks, before the production button appears at all. That is not a formality to be discovered in the last week: it is a fortnight of calendar time that has to start a fortnight before launch, and it wants the same four people the fun gate needs plus eight more.

The plan's Phase 4.8 closed track is therefore worth opening early, with whatever build exists, rather than when the game is ready.

## 2. The app, and who signs it

Create the app in the console with the package name **`nz.molehill.madness`**, which is what `client/export_presets.cfg` already declares.

**The package name is permanent.** It cannot be changed, reused, or recovered after an app is deleted, so it is worth being sure about the name before the first upload rather than after it.

Enrol in **Play App Signing**, which is on by default for new apps and should stay on. Google holds the key that signs what players install; the key made below is an upload key, which only proves an upload came from you. The difference matters exactly once: an upload key that is lost or leaked can be replaced by asking Google, while a lost app signing key without Play App Signing means the app can never be updated again by anybody, ever.

## 3. The upload key

Made once, kept somewhere that is not this repository. `.gitignore` already refuses `*.keystore`.

```bash
keytool -genkeypair -v -keystore upload.keystore -alias upload \
  -keyalg RSA -keysize 2048 -validity 10000 \
  -dname "CN=Molehill Madness, O=Inthrall, C=NZ"
```

It asks for a password twice. Use the same one for the store and the key, because the pipeline passes one password for both.

Then put it in the repository's secrets, as four values:

```bash
base64 -w0 upload.keystore > upload.keystore.base64

gh secret set PLAY_UPLOAD_KEYSTORE     --repo Inthrall/molehill-madness < upload.keystore.base64
gh secret set PLAY_UPLOAD_KEY_ALIAS    --repo Inthrall/molehill-madness --body upload
gh secret set PLAY_UPLOAD_KEY_PASSWORD --repo Inthrall/molehill-madness
```

Base64 because a secret is text and a keystore is not. `-w0` because a wrapped one decodes to a broken file, and the error it produces is about the keystore's format rather than about the wrapping.

Keep `upload.keystore` and its password somewhere durable and off this machine. Everything else here can be rebuilt.

## 4. The service account, so a workflow can upload

In the Google Cloud console, under the project that Play offers to link: create a service account, give it no roles at the project level, and download a JSON key.

In the Play console, under Users and permissions, invite that service account's email address and give it access to this app with the **Release to testing tracks** permission. Nothing more: an account that can only reach the testing tracks cannot publish to production by accident.

```bash
gh secret set PLAY_SERVICE_ACCOUNT --repo Inthrall/molehill-madness < service-account.json
```

The Google Play Android Developer API has to be enabled on that Cloud project, and access can take up to 24 hours to take effect. A first upload that fails with a permission error and then works the next morning is this, not a mistake.

**The first bundle for a new app cannot be uploaded by the API.** Play requires the first one through the console by hand, after which the API works. So expect to download the `bundle` artifact from the first tagged run and upload it in the browser once.

## 5. The console, before anything can go to testers

- **Content rating**, through the IARC questionnaire. The design targets ESRB E and accepts a PEGI 7 floor, and the questionnaire is answered honestly rather than optimistically: there is cartoon violence, no realistic violence, no gambling imagery of any kind, and no user to user text.
- **Target audience and content.** This is the delicate one. An all ages game that includes under 13s falls under the Families policy, which brings its own requirements around advertising, data collection and the store listing. The game has no advertising and no analytics beyond crashes, which is most of that answered already.
- **Data safety.** Declare what the relay actually holds, which is short: an opaque account id, a secret, an age band, and a device token for notifications. An email address only when a player links one, which is an adults only option and is refused server side otherwise. No name, no date of birth, no location, no advertising id.
- **A privacy policy at a public URL.** Required, and there is not one yet. It has to be reachable without signing in and has to say what the data safety form says.
- **Store listing**: title, short and full description, a feature graphic, screenshots for phone and tablet. The clip pipeline renders 1080x1920 already, which is the shape the store wants for a phone screenshot.

## 6. Cutting a release

```bash
git tag v0.8.0
git push origin v0.8.0
```

That runs the release workflow, which builds the Windows zip, the sideload APK and the Play bundle, and sends the bundle to the **internal** track with the release marked complete. Promotion from internal to a closed track, and from there to production, stays a decision made in the console.

Every push to any branch builds the bundle when the secrets exist, and only a tag uploads it. A push that uploaded would spend a version code and tell every tester there was something new several times an afternoon, and a tester who stops reading the notification has stopped testing.

The version code comes from the workflow run number, because Play refuses an upload whose code is not higher than the last one. It is not the tag: `0.7.10` sorts below `0.7.9` as a number and nobody expects that. The version name comes from the tag with its `v` removed.

## What is not verified

**The bundle export has never run.** The preset is written, the workflow step is written, and the local attempt was abandoned because Godot's gradle build downloads its own gradle distribution and had not finished in fifteen minutes on this connection. The Android build template installs correctly, which is the half that could have been wrong in an interesting way; what remains untested is the gradle run itself, and CI is a better place to test it than a laptop.

So the first tagged build is the real test of this file, and the two things most likely to be wrong are the keystore environment variables Godot reads, and whether the runner's gradle can reach everything it wants. Both fail loudly.

**There is no crash reporting yet, and there does not need to be.** Play's Android vitals collects crashes and ANRs for anything on the store, which answers the plan's Phase 4.8 item without adding an SDK, an account, or a second privacy disclosure. That is worth keeping: the design permits crash reporting as the one exception to no analytics, and the free one collects nothing beyond it.
