# osu!JukeBox on macOS — first launch

## Installing

Open `osu-JukeBox-macos-arm64.dmg` and drag **osu-JukeBox** onto the **Applications** shortcut in
the same window. That's the install.

Apple silicon only (arm64). An Intel Mac needs an `osx-x64` build, which this workflow does not
currently produce.

## The warning you will get, and why

The first time you open it, macOS will refuse, with something like:

> **"osu-JukeBox" cannot be opened because Apple cannot check it for malicious software.**

**This app is not signed with an Apple Developer ID and is not notarised.** It carries only an
ad-hoc signature — enough that macOS will execute it at all on Apple silicon, but not enough for
Gatekeeper, which only trusts binaries signed with a paid Apple Developer account and submitted to
Apple for notarisation. Every unsigned app downloaded from the internet gets this treatment; it is
not a statement about this particular app.

Signing would need the project owner's Apple Developer credentials, so it is their call, not
something a build can do on its own.

## Getting past it

Either of these works. The second is less clicking.

**Right-click to open.** Right-click (or Control-click) the app in Applications, choose **Open**,
then **Open** again in the dialog. macOS remembers the choice, so this is once per install.

**Or clear the quarantine flag in Terminal:**

```sh
xattr -dr com.apple.quarantine /Applications/osu-JukeBox.app
```

Then open it normally.

Both do the same thing: the download put a `com.apple.quarantine` attribute on the file, and these
remove it. Neither disables Gatekeeper for anything else on your Mac.

## If it bounces in the Dock and quits

That is usually a partial download or a copy that lost its signature. Delete the app, re-download
the `.dmg`, and drag it across again rather than copying the app out of an old one.
