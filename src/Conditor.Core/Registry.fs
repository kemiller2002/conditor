namespace Conditor.Core

module Registry =
    let private lifecycle id displayName package command version initArgs =
        { Id = id
          DisplayName = displayName
          Distribution = LifecycleNpm
          Package = package
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
              "ros"
              "3.4.0"
              [ "init"; "--target"; "{target}" ]
          lifecycle "ordo" "Ordo / State-Directed Engineering" "@echelon-foundry/sde" "sde" "1.4.0" [ "init" ]
          lifecycle
              "visual-engineering"
              "Visual Engineering"
              "@echelon-foundry/visual-engineering"
              "visual-engineering"
              "1.0.0"
              [ "init" ]
          lifecycle
              "communication-engineering"
              "Communication Engineering"
              "@echelon-foundry/communication-engineering"
              "communication-engineering"
              "1.0.0"
              [ "init" ]
          lifecycle
              "limen"
              "Limen"
              "@echelon-foundry/typescript-wasm-kernel"
              "limen"
              "0.6.2"
              [ "init" ]
          npmPackage "forma" "Forma" "@echelon-foundry/design-system" "0.2.0"
          npmPackage "folio" "Folio" "@echelon-foundry/print-components" "0.3.0"
          nugetPackage "aegis" "Aegis" "EchelonFoundry.Aegis.Core" "1.0.0" ]

    let tryFind id =
        all |> List.tryFind (fun definition -> definition.Id = id)
