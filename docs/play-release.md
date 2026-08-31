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

PowerShell, because that is the shell on this machine and it has no `<` redirection and no `base64` command. `keytool` comes from the JDK this project borrows from Unity.

```powershell
$keytool = "C:\Program Files\Unity\Hub\Editor\6000.5.8f1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK\bin\keytool.exe"

& $keytool -genkeypair -v -keystore C:\Personal\Molehill-secrets\upload.keystore -alias upload `
  -keyalg RSA -keysize 2048 -validity 10000 -dname "CN=Molehill Madness, O=Inthrall, C=NZ"
```

It asks for a password twice. **Use the same one for the store and the key**, because Godot reads one password and uses it for both: a keystore with two different passwords fails the export with "Release Username and/or Password is invalid", which is one message covering three possible causes.

Then the secrets. `gh` defaults to the work account, which cannot see this repository at all, so the token comes first.

```powershell
$env:GH_TOKEN = gh auth token -u Inthrall

$base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes("C:\Personal\Molehill-secrets\upload.keystore"))
$base64 | gh secret set PLAY_UPLOAD_KEYSTORE --repo Inthrall/molehill-madness

gh secret set PLAY_UPLOAD_KEY_ALIAS    --repo Inthrall/molehill-madness --body upload
gh secret set PLAY_UPLOAD_KEY_PASSWORD --repo Inthrall/molehill-madness --body 'the password'
```

Base64 because a secret is text and a keystore is not, and unwrapped because a wrapped one decodes to a broken file whose error message is about the keystore's format rather than about the encoding.

Keep `upload.keystore` and its password somewhere durable and off this machine. Everything else here can be rebuilt.

## 4. The service account, so a workflow can upload

In the Google Cloud console, under the project that Play offers to link: create a service account, give it no roles at the project level, and download a JSON key.

In the Play console, under Users and permissions, invite that service account's email address and give it access to this app with the **Release to testing tracks** permission. Nothing more: an account that can only reach the testing tracks cannot publish to production by accident.

```powershell
Get-Content -Raw service-account.json | gh secret set PLAY_SERVICE_ACCOUNT --repo Inthrall/molehill-madness
```

One service account can serve several apps, so an existing key works: grant it access to this app and reuse the JSON. A GitHub secret cannot be read back, though, so if the only copy of the key is already a secret in another repository, mint a second key from the Cloud console rather than looking for the first one.

The Google Play Android Developer API has to be enabled on that Cloud project, and access can take up to 24 hours to take effect. A first upload that fails with a permission error and then works the next morning is this, not a mistake.

**The first bundle for a new app cannot be uploaded by the API.** Play requires the first one through the console by hand, after which the API works. So expect to download the `bundle` artifact from the first tagged run and upload it in the browser once.

## 5. The console, before anything can go to testers

- **Content rating**, through the IARC questionnaire. The design targets ESRB E and accepts a PEGI 7 floor, and the questionnaire is answered honestly rather than optimistically: there is cartoon violence, no realistic violence, no gambling imagery of any kind, and no user to user text.
- **Target audience and content.** This is the delicate one. An all ages game that includes under 13s falls under the Families policy, which brings its own requirements around advertising, data collection and the store listing. The game has no advertising and no analytics beyond crashes, which is most of that answered already.
- **Data safety.** Declare what the relay actually holds, which is short: an opaque account id, a secret, an age band, and the match itself. An email address only when a player links one, which is an adults only option and is refused server side otherwise. No name, no date of birth, no location, no advertising id. The relay can hold a push token, but nothing in the client ever registers one, so it is not collected by anything that ships.
- **A privacy policy at a public URL.** [`privacy.html`](privacy.html) is it, served at https://inthrall.github.io/molehill-madness/privacy.html from this repository's own `docs` folder through GitHub Pages. It is written against the relay's schema, so it and the data safety form say the same thing by construction.
- **Store listing**: title, short and full description, a feature graphic, screenshots for phone and tablet. The clip pipeline renders 1080x1920 already, which is the shape the store wants for a phone screenshot.

## 6. Cutting a release

```bash
git tag v0.8.0
git push origin v0.8.0
```

That runs the release workflow, which builds the Windows zip, the sideload APK and the Play bundle, and sends the bundle to the **internal** track with the release marked complete. Promotion from internal to a closed track, and from there to production, stays a decision made in the console.

Every push to any branch builds the bundle when the secrets exist, and only a tag uploads it. A push that uploaded would spend a version code and tell every tester there was something new several times an afternoon, and a tester who stops reading the notification has stopped testing.

The version code comes from the workflow run number, because Play refuses an upload whose code is not higher than the last one. It is not the tag: `0.7.10` sorts below `0.7.9` as a number and nobody expects that. The version name comes from the tag with its `v` removed.

## The console forms, answer by answer

Every value below is checked against what the game and the relay actually do, rather than what a game of this shape usually does. Where an answer will change later, it says when.

### App access

| Field | Answer |
| --- | --- |
| Is any functionality restricted? | All functionality is available without special access |

Nothing in the game needs credentials. Playing on the couch needs nothing at all, and playing online creates an anonymous account on the device without anybody signing in to anything.

### Ads

| Field | Answer |
| --- | --- |
| Does your app contain ads? | No |

Permanently no. The design's money model is cosmetics, and no advertising anywhere is one of the reasons the all ages rating is reachable at all.

### Content rating (the IARC questionnaire)

| Question | Answer |
| --- | --- |
| Category | Game |
| Email for the certificate | hello-mole@decryptic.app |
| Violence | Yes, cartoon or fantasy violence only. Moles lob clods and dynamite at each other, are knocked out comically, and reappear next match. No blood, no injury detail, no realistic weapons |
| Sexuality, nudity | No |
| Language | None. There are no words in the game at all |
| Controlled substances | No |
| Gambling, simulated or real | No. Crates are earned by reaching them rather than opened by chance, and the design rules out gambling imagery outright, which is why the strangers glyph is molehills rather than dice |
| Users can interact | Yes. Online matches put players together |
| Users can share content | No, for now. Clip sharing exists in the code but the share sheet is not wired up on Android, so nothing leaves the device yet. Retake the questionnaire when it does |
| Shares user location | No |
| Digital purchases | No, for now. Cosmetics arrive in a later phase and this answer changes with them |
| Personal information shared with third parties | No |

Expect E from ESRB and PEGI 3 or 7. The design accepts a PEGI 7 floor.

### Target audience and content

| Field | Answer |
| --- | --- |
| Target age groups | 5 to 8, 9 to 12, 13 to 15, 16 to 17, 18 and over |
| Appeals to children? | Yes |
| Store presence for children | Follows from the above |

**This is the one real decision in the list, so it is worth making deliberately.** Including under 13 puts the app in Google Play's Families programme, which brings extra review, a stricter set of policies to meet, and the requirement that everything in the app is suitable for children. The game is built for that: no advertising, no analytics, no free text chat, an age band that gates strangers, and a privacy policy that describes four fields.

The alternative is to declare 13 and over, which avoids Families review entirely and is quicker to get through. It also contradicts the design, which has an under 13 lane in it and a whole age gate built to serve one, so it is only worth doing as a temporary measure to get a closed test running.

### Data safety

The answers here come from the relay's schema. The tables are `accounts`, `email_claims`, `queue`, `devices`, `nudges`, `emotes`, `matches`, `seats` and `submissions`, and nothing in them is a name, a date of birth, a location or a device identifier.

| Field | Answer |
| --- | --- |
| Does your app collect or share required user data? | Yes |
| Is all data encrypted in transit? | Yes |
| Can users request data deletion? | Yes, at https://inthrall.github.io/molehill-madness/delete-account.html |
| Has a privacy policy | https://inthrall.github.io/molehill-madness/privacy.html |
| Account creation methods | **Other**. An account is made for the player without a username, a password or a sign in, and it is not "does not allow": the record persists, holds an age band, and can have an email attached |

The deletion answer has to be a web page rather than an email address, because an app that creates accounts must offer account deletion on the web. That is what `delete-account.html` is for.

Data types to declare:

| Category and type | Collected | Shared | Optional? | Purpose | What it is |
| --- | --- | --- | --- | --- | --- |
| Personal info, User IDs | Yes | No | Optional | App functionality, Account management | The relay's opaque account id. Google's definition of User IDs names account IDs outright, so this counts even though it identifies nobody |
| Personal info, Other info | Yes | No | Optional | App functionality | The age band. It is the "such as date of birth" case in coarser form |
| Personal info, Email address | Yes | No | Optional | Account management | Only for the sixteen and over band, refused for everybody else |
| App activity, Other actions | Yes | No | Optional | App functionality | The match: game code, seed, each round's plans, the result |
| Messages, Other in-app messages | Yes | No | Optional | App functionality | Emotes. A preset symbol is still a message from one player to another, and the relay stores them |

Optional rather than required, all of it, because a player who only plays on the couch creates no account and sends nothing anywhere. Collection begins when somebody chooses to play online, which is Play's own definition of optional. None of it is processed ephemerally: it is in a database.

Everything else is No. In particular:

- **Device or other IDs: No.** That category means advertising and device identifiers. The account id is a value the app generates for itself, which is why it goes under User IDs instead.
- **App info and performance: No.** Crash reports come from Play's own vitals, and data Google Play collects automatically is not declared here.
- **User generated content: No**, while clip sharing is unwired. It becomes a Yes when a player can send a clip.
- **Location, contacts, photos, files, calendar, health, financial: No.** None of it is asked for and none of it is in the manifest, which requests only INTERNET.

The first version of this table declared two types and missed three. User IDs, the age band and the emotes were all being held and none of them were being declared, which is the kind of gap a reviewer finds rather than a developer.

### Store listing

| Field | Value |
| --- | --- |
| App name (30 characters) | Molehill Madness |
| Short description (80) | Four moles, one clock. Everybody plans at once and it all goes off together. |
| Category | Games, then Strategy. It is turn based artillery, so Strategy fits it better than Action or Casual |
| Tags | Turn-based, Multiplayer, Artillery |
| Email | hello-mole@decryptic.app |
| Website | https://inthrall.github.io/molehill-madness/ |
| Privacy policy | https://inthrall.github.io/molehill-madness/privacy.html |

A full description worth starting from, which says what the game is rather than shouting about it:

> Four players. Four moles each. One eight second clock, and everybody plans at the same time.
>
> Nobody waits for a turn in Molehill Madness. All four of you plot at once, then every plan resolves together: shots have to lead moles that are still moving, four ambushes collide in mid air, and a carefully drawn route marches proudly into a crater that did not exist when it was drawn.
>
> Dig. The ground is destructible all the way down, and tunnelling costs stamina you could have spent on walking. Underground is cover, a shortcut, and a very good place to be when somebody drops dynamite where you used to be standing.
>
> At round eight the lava arrives. It rises from the bottom of the map, then closes in from the sides, and it bounces you when it touches you, three times before you are out.
>
> Play on the couch with four controllers, or apart at your own pace. There are no words anywhere in the game, so anybody can pick it up, and no chat, so nobody can be unpleasant in it.

The graphics it needs, none of which exist yet: a 512x512 icon, which is a resize of `art/app icon.png`; a 1024x500 feature graphic; and at least two phone screenshots, for which the clip pipeline already renders 1080x1920.

### The rest of the questionnaire

| Form | Answer |
| --- | --- |
| News app | No |
| COVID-19 contact tracing or status | No |
| Government app | No |
| Financial features | None of these |
| Health apps | No |
| Pricing | Free |
| In-app purchases | None yet. Changes when cosmetics ship |

### Testing tracks

| Field | Value |
| --- | --- |
| Track for the closed test | The one the twelve tester requirement counts, if it applies to this account |
| Feedback email | hello-mole@decryptic.app |
| Countries | New Zealand at least; add anywhere a tester lives |

A release sitting in **draft** reaches nobody. It has to be rolled out before testers can install it, and before any fourteen day clock starts counting.

## What is not verified

**The bundle export works.** It could not be run locally, because Godot's gradle build downloads its own gradle distribution and had not finished in fifteen minutes on this connection, so it was proven on CI instead: run 33343658404 built and signed a 72 MB bundle in about a hundred seconds, containing MoleSim.dll, and the first upload went to the internal track by hand as version code 22.

One thing did fail on the way and is worth keeping. The first attempt died with "Release Username and/or Password is invalid for the given Release Keystore", which is Godot's single message for a wrong password, a wrong alias, and a keystore with separate store and key passwords. The fix was a keystore made with one password for both, verified with `keytool -list` before the secret was set. A pre-flight `keytool` check in the workflow would name which of the three is wrong in ten seconds rather than after a build.

**What is still untested is the API upload.** Every bundle so far went up by hand, because Play requires that for a new app. The service account's access is confirmed, so the next tagged release is the first exercise of the automatic path.

**There is no crash reporting yet, and there does not need to be.** Play's Android vitals collects crashes and ANRs for anything on the store, which answers the plan's Phase 4.8 item without adding an SDK, an account, or a second privacy disclosure. That is worth keeping: the design permits crash reporting as the one exception to no analytics, and the free one collects nothing beyond it.
