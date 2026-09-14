# Feather.GraphQL

A GraphQL client whose queries are compiled, not translated at run time. See `README.md` for the
design and `docs/design/` for the reasoning behind it.

## Build and test

Requires the .NET 10 SDK. The solution is `Feather.GraphQL.slnx`.

```bash
dotnet restore
dotnet build --no-restore -c Release
dotnet test -c Release --no-build
```

`TreatWarningsAsErrors` is on for every project, so a warning fails the build.

The NativeAOT claim is verified by publishing a sample rather than by a test, because a trim
warning is not something a test can observe:

```bash
dotnet publish samples/Feather.GraphQL.AotSmoke -c Release -r linux-x64 -p:PublishAot=true -warnaserror
```

That proves the claim against the projects in `src/`. `samples/Feather.GraphQL.PackageSmoke` proves
it against the packages, which is a different thing — see below:

```bash
dotnet pack -c Release -o nupkg -p:Version=0.0.1-local
dotnet publish samples/Feather.GraphQL.PackageSmoke -c Release -r linux-x64 \
  -p:PublishAot=true -p:FeatherVersion=0.0.1-local -warnaserror
```

## Things that are easy to break

- **`examples/Feather.GraphQL.Example` references the published NuGet packages**, not the projects
  in `src/`, so that it tests what a consumer installs. Nothing else may reference it by project:
  that puts the packaged and the source copy of an assembly on one compilation and fails with
  `CS1704`. Duplicate the few lines you need instead, as the benchmark projects do.
- **Package versions are central**, in `Directory.Packages.props`. A `PackageReference` carries no
  `Version` attribute.
- **The version comes from the commit history**, read by `paulhatch/semantic-version` in CI. A
  breaking change is declared by putting `(MAJOR)` or `(MINOR)` in the commit body — it is never
  inferred from the diff.
- **A compiled chain needs the interceptor namespace turned on, and the package has to carry it.**
  It lives in `src/Feather.GraphQL.Linq/buildTransitive/Feather.GraphQL.Linq.props`, packed into
  `Feather.GraphQL.Linq`, in `buildTransitive` rather than `build` because most consumers install
  the transport package and get this one as a dependency. `Directory.Build.props` imports that same
  file rather than restating it: nothing here would notice it going missing, because every project
  in this repository is already handed the opt-in. That is how it went missing once, and a consumer
  installing the result got `CS9137` on generated code. `samples/Feather.GraphQL.PackageSmoke` is
  the guard — it is outside the solution and stops `Directory.Build.props` at its own folder, so it
  sees only what the package provides.

## Cloud sessions

The cloud sandbox has no .NET SDK by default. `docs/cloud-sessions.md` covers the setup script that
installs it and the SDK feature band difference to watch for.
