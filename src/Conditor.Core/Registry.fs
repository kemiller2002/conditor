namespace Conditor.Core

module Registry =
    let private lifecycle id displayName package source command version initArgs =
        { Id = id
          DisplayName = displayName
          Distribution = LifecycleNpm
          Package = package
          LifecycleSource = Some source
          Command = Some command
          DefaultVersion = version
          InitArguments = initArgs
          VerifyArguments = [ "verify"; "--strict" ]
          DoctorArguments = [ "doctor" ] }

    let private npmPackage id displayName package version =
        { Id = id
          DisplayName = displayName
          Distribution = NpmPackage
          Package = package
          LifecycleSource = None
          Command = None
          DefaultVersion = version
          InitArguments = []
          VerifyArguments = []
          DoctorArguments = [] }

    let private nugetPackage id displayName package version =
        { Id = id
          DisplayName = displayName
          Distribution = NugetPackage
          Package = package
          LifecycleSource = None
          Command = None
          DefaultVersion = version
          InitArguments = []
          VerifyArguments = []
          DoctorArguments = [] }

    let all =
        [ lifecycle
              "praxis"
              "Praxis / Repository Operating System"
              "@echelon-foundry/repository-operating-system"
              RegistryPackage
              "ros"
              "3.1.4"
              [ "init"; "--target"; "{target}" ]
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
              (FixedPackageSpec "github:kemiller2002/communication-engineering#4590d2fe6f7e80b339117d3fbee5803f2dd39122")
              "communication-engineering"
              "1.0.0"
              [ "init" ]
          lifecycle
              "limen"
              "Limen"
              "@echelon-foundry/typescript-wasm-kernel"
              RegistryPackage
              "limen"
              "0.6.1"
              [ "init" ]
          npmPackage "forma" "Forma" "@echelon-foundry/design-system" "0.2.0"
          npmPackage "folio" "Folio" "@echelon-foundry/print-components" "0.3.0"
          nugetPackage "aegis" "Aegis" "EchelonFoundry.Aegis.Core" "1.0.0" ]

    let tryFind id =
        all |> List.tryFind (fun definition -> definition.Id = id)
