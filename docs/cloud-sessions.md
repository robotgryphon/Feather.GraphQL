# Working on this repository from a cloud session

A Claude Code cloud session — started from the Claude mobile app, from claude.ai/code, or with
`claude --cloud` — runs in an Anthropic-managed Ubuntu 24.04 VM that clones this repository from
GitHub. The image ships Python, Node, Go, Rust, Java and a C/C++ toolchain, but **not the .NET
SDK**, so a session has to install it before `dotnet build` means anything.

## One-time setup

1. Open [claude.ai/code](https://claude.ai/code) and open the environment selector.
2. Create or edit an environment (the **Default** one is fine).
3. Leave **Network access** on **Trusted**. That level already allows `archive.ubuntu.com`,
   `nuget.org` and `api.nuget.org`, which is everything a restore and a build need.
4. Paste the contents of [`.claude/cloud-setup.sh`](../.claude/cloud-setup.sh) into the
   **Setup script** field.
5. Add these **Environment variables**, matching what the CI workflows set:

   ```
   DOTNET_NOLOGO=true
   DOTNET_CLI_TELEMETRY_OPTOUT=true
   MSBUILDSINGLELOADCONTEXT=1
   ```

The setup script runs the first time a session starts in the environment. Anthropic then snapshots
the filesystem, so later sessions start with the SDK already on disk and skip the script. It runs
again when the script changes, when the allowed hosts change, or after the cache expires at roughly
seven days.

## Verifying a change in a session

These are the same commands the branch workflow runs, in the same order:

```bash
dotnet restore
dotnet build --no-restore -c Release
dotnet test -c Release --no-build
```

The AOT claim is checked by publishing the smoke sample rather than by a test. It needs `clang` and
`zlib1g-dev`, which the setup script installs:

```bash
dotnet publish samples/Feather.GraphQL.AotSmoke -c Release -r linux-x64 -p:PublishAot=true -warnaserror
./samples/Feather.GraphQL.AotSmoke/bin/Release/net10.0/linux-x64/publish/Feather.GraphQL.AotSmoke
```

## SDK feature bands

CI resolves `10.0.x` from the .NET CDN, which is the latest feature band. The setup script tries
the same CDN first, but reaching it needs `aka.ms` and `builds.dotnet.microsoft.com`, and neither
is on the **Trusted** allowlist. Under **Trusted** the script therefore falls back to Ubuntu's
`dotnet-sdk-10.0`, which sits in a lower band than CI's.

That is fine for ordinary work. It matters when a build failure appears only in a cloud session:
check it against CI before believing it. To remove the difference, set **Network access** to
**Custom** and list `aka.ms` and `builds.dotnet.microsoft.com` alongside the hosts the build
needs — `archive.ubuntu.com`, `security.ubuntu.com`, `nuget.org`, `api.nuget.org` and
`github.com` — remembering that **Custom** replaces the Trusted list rather than extending it.

## The example project

`examples/Feather.GraphQL.Example` references the **published** `Feather.GraphQL.*` packages
instead of the projects in `src/`, so that it exercises what a consumer actually installs. Two
consequences are worth knowing before changing it:

- A restore of the solution reaches `api.nuget.org` for those packages. With network access set to
  **None**, the whole solution fails to restore, not just the example.
- Nothing else in the repository may reference the example by project. Doing so puts both the
  packaged and the source copy of an assembly on one compilation, which fails with `CS1704`.
  `benchmarks/Feather.GraphQL.Benchmarks` and `benchmarks/Feather.GraphQL.Benchmarks.Profiling`
  each declare their own `CountryFilter` for this reason.
