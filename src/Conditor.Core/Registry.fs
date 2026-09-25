namespace Conditor.Core

module Registry =
    let private lifecycleWithVerification id displayName package source command version initArgs verifyArgs =
        { Id = id
          DisplayName = displayName
          Distribution = LifecycleNpm
          Package = package
          LifecycleSource = Some source
          ApplicationBinding = None
          Command = Some command
          DefaultVersion = version
          InitArguments = initArgs
          VerifyArguments = verifyArgs
          DoctorArguments = [ "doctor" ]
          UpgradeArguments = [ "upgrade" ] }

    let private lifecycle id displayName package source command version initArgs =
        lifecycleWithVerification
            id
            displayName
            package
            source
            command
            version
            initArgs
            [ "verify"; "--strict" ]

    let private githubSource repository commit entrypoint =
        GitHubSource
            { Repository = repository
              Commit = commit
              Entrypoint = NodeScript entrypoint }

    let private npmPackage id displayName package version =
        { Id = id
          DisplayName = displayName
          Distribution = NpmPackage
          Package = package
          LifecycleSource = None
          ApplicationBinding = Some NpmDependency
          Command = None
          DefaultVersion = version
          InitArguments = []
          VerifyArguments = []
          DoctorArguments = []
          UpgradeArguments = [] }

    let private nugetPackage id displayName package version =
        { Id = id
          DisplayName = displayName
          Distribution = NugetPackage
          Package = package
          LifecycleSource = None
          ApplicationBinding = Some NugetReference
          Command = None
          DefaultVersion = version
          InitArguments = []
          VerifyArguments = []
          DoctorArguments = []
          UpgradeArguments = [] }

    let all =
        [ lifecycle
              "praxis"
              "Praxis / Repository Operating System"
              "@echelon-foundry/repository-operating-system"
              RegistryPackage
              "ros"
              "3.1.4"
              [ "init" ]
          lifecycle
              "ordo"
              "Ordo / State-Directed Engineering"
              "@echelon-foundry/sde"
              RegistryPackage
              "sde"
              "1.3.0"
              [ "init" ]
          lifecycle
              "visual-engineering"
              "Visual Engineering"
              "@echelon-foundry/visual-engineering"
              RegistryPackage
              "visual-engineering"
              "1.0.0"
              [ "init" ]
          lifecycle
              "communication-engineering"
              "Communication Engineering"
              "@echelon-foundry/communication-engineering"
              (githubSource
                  "kemiller2002/communication-engineering"
                  "4590d2fe6f7e80b339117d3fbee5803f2dd39122"
                  "bin/communication-engineering.mjs")
              "communication-engineering"
              "1.0.0"
              [ "init" ]
          { lifecycleWithVerification
                "limen"
                "Limen"
                "@echelon-foundry/typescript-wasm-kernel"
                RegistryPackage
                "limen"
                "0.6.1"
                [ "init" ]
                [ "verify" ] with
                ApplicationBinding = Some NpmDependency }
          npmPackage "forma" "Forma" "@echelon-foundry/design-system" "0.2.0"
          npmPackage "folio" "Folio" "@echelon-foundry/print-components" "0.3.0"
          nugetPackage "aegis" "Aegis" "EchelonFoundry.Aegis.Core" "1.0.0"
          lifecycle
              "tutela"
              "Tutela Security Engineering"
              "@echelon-foundry/tutela"
              (githubSource
                  "kemiller2002/tutela"
                  "1acf421e7d940665c134012f11b082a502e17537"
                  "bin/tutela.mjs")
              "tutela"
              "0.1.0"
              [ "init" ] ]

    let tryFind id =
        all |> List.tryFind (fun definition -> definition.Id = id)
