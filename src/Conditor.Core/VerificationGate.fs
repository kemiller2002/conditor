namespace Conditor.Core

open System
open System.Text.Json

/// The split verification gate (user decision "split the gate").
///
/// A component whose qualified version declares an `integrityGate` is
/// verified with that gate instead of its plain verify arguments. The gate's
/// JSON report decides the outcome: any failure that is not a structural
/// review failure — missing install, damaged or modified managed artifacts,
/// version or toolchain-pin disagreement, unusable configuration, or a
/// category Conditor does not know — fails closed. Structural review
/// findings are returned as review signals to be reported and recorded,
/// never as a refusal. Anything Conditor cannot read as a passing report
/// also fails closed.
module VerificationGate =
    [<Literal>]
    let private StructuralReviewCategory = "structural-review"

    let gateFor (version: string) (definition: ComponentDefinition) =
        definition.IntegrityGate
        |> Option.filter (fun gate -> gate.Versions.Contains version)

    /// The arguments that verify `version` of the component.
    let argumentsFor (version: string) (definition: ComponentDefinition) =
        match gateFor version definition with
        | Some gate -> gate.Arguments
        | None -> definition.VerifyArguments

    let private diagnostic (result: ProcessResult) =
        [ result.StandardOutput.Trim(); result.StandardError.Trim() ]
        |> List.filter (String.IsNullOrWhiteSpace >> not)
        |> String.concat " | "

    let private property (name: string) (element: JsonElement) =
        let mutable value = Unchecked.defaultof<JsonElement>

        if element.ValueKind = JsonValueKind.Object && element.TryGetProperty(name, &value) then
            Some value
        else
            None

    let private text name element =
        property name element
        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.String)
        |> Option.bind (fun value -> value.GetString() |> Option.ofObj)

    let private integer name element =
        property name element
        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Number)
        |> Option.bind (fun value ->
            match value.TryGetInt32() with
            | true, parsed -> Some parsed
            | _ -> None)

    let private items name element =
        property name element
        |> Option.filter (fun value -> value.ValueKind = JsonValueKind.Array)
        |> Option.map (fun value -> value.EnumerateArray() |> Seq.toList)

    type private Report =
        { Passed: bool option
          Failures: (string * string * string) list
          Signals: Result<ReviewSignal list, string> }

    let private readSignal componentId (signal: JsonElement) =
        match text "code" signal, text "band" signal, text "path" signal, integer "lineCount" signal with
        | Some code, Some band, Some path, Some lineCount ->
            Ok
                { ComponentId = componentId
                  Code = code
                  Band = band
                  Path = path
                  LineCount = lineCount }
        | _ -> Error "a review signal is missing code, band, path or lineCount"

    let private readReport componentId (output: string) =
        try
            use document = JsonDocument.Parse(output)
            let root = document.RootElement

            if root.ValueKind <> JsonValueKind.Object then
                None
            else
                let passed =
                    property "passed" root
                    |> Option.bind (fun value ->
                        match value.ValueKind with
                        | JsonValueKind.True -> Some true
                        | JsonValueKind.False -> Some false
                        | _ -> None)

                let failures =
                    items "failures" root
                    |> Option.defaultValue []
                    |> List.map (fun failure ->
                        text "category" failure |> Option.defaultValue "<uncategorised>",
                        text "code" failure |> Option.defaultValue "<no code>",
                        text "detail" failure |> Option.defaultValue "")

                let signals =
                    match items "reviewSignals" root with
                    | None -> Error "the report has no reviewSignals array"
                    | Some entries ->
                        entries
                        |> List.map (readSignal componentId)
                        |> List.fold
                            (fun state next ->
                                match state, next with
                                | Ok collected, Ok signal -> Ok(signal :: collected)
                                | Error error, _
                                | _, Error error -> Error error)
                            (Ok [])
                        |> Result.map List.rev

                Some
                    { Passed = passed
                      Failures = failures
                      Signals = signals }
        with :? JsonException ->
            None

    /// Interprets the output of an integrity gate run for `componentId`
    /// `version`. Returns the review signals of a passing gate, or every
    /// reason it failed closed.
    let interpretReport (componentId: string) (version: string) (result: ProcessResult) =
        let subject = $"'{componentId}' {version} integrity gate"

        match readReport componentId result.StandardOutput with
        | None ->
            let shown = diagnostic result
            Error [ $"{subject} exited {result.ExitCode} without a readable JSON report: {shown}" ]
        | Some report ->
            let blocking =
                report.Failures
                |> List.filter (fun (category, _, _) -> category <> StructuralReviewCategory)

            let reasons =
                blocking
                |> List.map (fun (category, code, detail) -> $"{subject} [{category}] {code}: {detail}")

            match result.ExitCode, report.Passed, reasons, report.Signals with
            | 0, Some true, [], Ok signals -> Ok signals
            | 0, Some true, [], Error detail -> Error [ $"{subject} report is malformed: {detail}" ]
            | exitCode, passed, [], _ ->
                let claimed =
                    passed |> Option.map string |> Option.defaultValue "absent"

                Error [ $"{subject} did not pass (exit {exitCode}, passed {claimed}) and named no integrity failure." ]
            | _, _, reasons, _ -> Error reasons

    /// Interprets the verification of `version` of a component: through its
    /// integrity gate when that version declares one, otherwise by exit code.
    let interpret (componentId: string) (version: string) (definition: ComponentDefinition) (result: ProcessResult) =
        match gateFor version definition with
        | Some _ -> interpretReport componentId version result
        | None when result.ExitCode = 0 -> Ok []
        | None ->
            let shown = diagnostic result

            if String.IsNullOrWhiteSpace shown then
                Error [ $"Verification exited {result.ExitCode}." ]
            else
                Error [ $"Verification exited {result.ExitCode}: {shown}" ]

    let describe (signal: ReviewSignal) =
        $"{signal.ComponentId} {signal.Code} [{signal.Band}] {signal.Path}: {signal.LineCount} lines"
