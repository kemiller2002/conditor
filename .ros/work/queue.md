# Work Queue

| ID | Work | Status | Tags | Priority |
|---|---|---|---|---|
| INDY-0001 | CI smoke checks follow the web scaffold's root package.json | complete | ci, scaffold | high |
| INDY-0002 | conditor requirements import: pinned planning documents to Praxis slice work items with a full trace | complete | requirements, indy-init | high |
| INDY-0003 | conditor supervise: sustainable headless Claude launch with explicit permission mode, resume on exit and a deadline | complete | launcher, indy-init | high |
| INDY-0004 | Indy day-of defaults: demo-readiness never-cut, baseline security and trace view as slice acceptance criteria, Pages deploy, AI, persistence, organizer and demo decisions | complete | indy-init, requirements | high |
| ROS-INSTALL-3-7-1 | ROS-INSTALL-3-7-1 | complete |  |  |
| WI-0001 | Govern conditor with Praxis 3.7.1 and Ordo 1.4.0 | complete | praxis, ordo, toolchain | medium |
| WI-0002 | Safe fail-closed current-upgrade contract for package-distributed web bindings (limen, forma, folio); qualify Praxis 3.7.1 and Ordo 1.4.1 | complete |  | medium |
| WI-0003 | Move conditor to Ordo 1.4.1 | complete | ordo, toolchain | medium |
| WI-0004 | WI-0004 | complete |  |  |
| WI-0005 | conditor adopt: report not-found when a lifecycle component is not installed in the repository even if its CLI is on the machine (installation markers) | complete |  | medium |
| WI-0006 | Qualify the echelon-current releases: Praxis 3.7.2 and Visual Engineering 1.0.1, with a planned VE 1.0.0 -> 1.0.1 current-upgrade transition | complete |  | medium |
| WI-0007 | Move conditor to Praxis 3.7.2 and Ordo 1.4.2 | complete | praxis, ordo, toolchain | medium |
| WI-0008 | Current upgrade verifies with the tools it installed | complete | lifecycle | high |
| WI-0009 | Current upgrade opts a project in to an optional native lifecycle tool | complete | lifecycle | high |
| WI-0010 | Qualify Ordo 1.5.0 (echelon-current 1.2.0) and make conditor's own Ordo pins consistent at 1.5.0 | complete |  | medium |
| WI-0011 | x | abandoned |  | medium |
| WI-0012 | Scaffold for a new NuGet library package repository: foundations manifest, build-and-test and foundations CI, and the Registry release workflow pattern | complete |  | medium |
| WI-0013 | AdoptionTests 'Registry-authorized adoption succeeds' discovers real praxis/ordo/percepta on PATH (e.g. after conditor workstation apply puts them in ~/.local/bin) and fails; the fixture should isolate PATH | captured |  | medium |
| WI-0014 | fsharp-nuget-library scaffold must ignore bin/, obj/, dist/ and packages: build output would otherwise be committed | complete | scaffold | high |
| WI-0015 | fsharp-limen-web scaffold has the same .gitignore gap (engine bin/obj): add the build-outputs region there too | complete |  | medium |
| WI-0016 | fsharp-nuget-library scaffold: make the test project a real dotnet test project (xUnit) so Dokimos and other TRX consumers get test evidence | complete | scaffold, dokimos | high |
| WI-0017 | Indy preset: materialize every Indy-init doc the requirements reference, pinned at the corrected kickoff-path commit | complete | preset, indy-init | high |
| WI-0018 | Indy preset follows echelon-current: Praxis 3.7.2, Ordo 1.5.0, Limen 0.7.1, Forma 0.4.1 | complete | preset, indy-init | high |
| WI-0019 | fsharp-limen-web scaffold: engine tests, browser suite, build-and-test CI with cached Playwright, foundations, inert Pages deploy, branch protection | complete | scaffold, indy-init | high |
| WI-0020 | conditor repo create: publish an initialized target as a protected GitHub repository | complete | github, indy-init | high |
| WI-0021 | Install Registry nuget-library releases distributed as GitHub release assets (ordo-core.nupkg, Arca) into a consumer's local NuGet feed: digest-proven vendor/nuget, lock, NuGet.config source mapping; init, verify and upgrade --current opt-in and version change | complete | nuget, feed, arca, registry | high |
| WI-0022 | An established scaffold's seed files belong to the project: stop comparing or recreating them once the lock records the scaffold, so a project that grew past its scaffold can still plan, repair and upgrade --current; roll back governance when the current-upgrade commit step fails | complete | scaffold, upgrade, current | high |
| WI-0023 | Release Conditor 0.5.0 so consumers get local-feed NuGet installs (#59) and scaffold ownership (#60) | complete |  | medium |
| WI-0024 | Release Conditor 0.6.0 so consumers get requirements import, supervise and the Indy day-of defaults | complete |  | medium |
| WI-0025 | URL-addressable state by default: fsharp-limen-web scaffolds the route inventory, the routing foundation and a pure route codec placeholder until Limen 0.9.0; SAF-URL-1..10 in the web requirement checklist | complete | scaffold, deep-linking | high |
| WI-0026 | When the indy-init preset pin moves past Indy-init 2eee2d52 (PR #7, URL-addressable state), update requirementsImport expected counts: R 77->82, P 458->464, OQ 47->48 | complete | indy, requirements-import | medium |
| WI-0027 | Release Conditor 0.7.0: URL-addressable state in the web scaffold (#68) | complete | release | high |
| WI-0028 | Scaffold route inventory in Limen 0.9.0's exact echelon.routes/v1 shape, and pin the foundations CI to the Praxis verifier that vendors Limen's schema | complete | scaffold, deep-linking | high |
| WI-0029 | Wire Limen.Routing into the fsharp-limen-web scaffold once Conditor qualifies Limen 0.9.0 and limen-fsharp 0.9.0 (CON-293) | complete | scaffold, deep-linking, limen | medium |
| WI-0030 | Release Conditor 0.7.1: scaffold route inventory in Limen 0.9.0's exact shape (#70) | complete | release | high |
