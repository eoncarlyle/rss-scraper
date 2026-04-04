module Serialisation

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open DomainModel
open System.Globalization

let jsonOptions =
    let options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true)

    options.WriteIndented <- true

    options.Converters.Add(
        JsonFSharpConverter(
            JsonFSharpOptions
                .Default()
                .WithUnionUnwrapFieldlessTags()
                .WithSkippableOptionFields()
        )
    )

    options

// TODO resultify all of this
let deserialise<'T> (json: string) : 'T =
    JsonSerializer.Deserialize<'T>(json, jsonOptions)

let serialise settings =
    JsonSerializer.Serialize(settings, jsonOptions)

let toAnthropicStatus (status: ProcessingStatus) =
    match status with
    | InProgress -> Anthropic.Models.Messages.Batches.ProcessingStatus.InProgress
    | Canceling -> Anthropic.Models.Messages.Batches.ProcessingStatus.Canceling
    | Ended -> Anthropic.Models.Messages.Batches.ProcessingStatus.Ended

let fromAnthropicStatus (status: Anthropic.Models.Messages.Batches.ProcessingStatus) =
    match status with
    | Anthropic.Models.Messages.Batches.ProcessingStatus.InProgress -> InProgress
    | Anthropic.Models.Messages.Batches.ProcessingStatus.Canceling -> Canceling
    | _ -> Ended

let htmlSanitized (s: string) =
    if isNull s then
        ""
    else
        let doc = HtmlAgilityPack.HtmlDocument()
        doc.LoadHtml s
        doc.DocumentNode.InnerText

let firstSentenceRemove (s: string) =
    if String.IsNullOrEmpty(s) then
        s
    else
        let m = Regex.Match(s, "^[^.]*(?:\.[^.]*)*?\.bm[^.]*\.\s*")
        if m.Success then s.Substring m.Length else s

let rfc822Date (dto: DateTimeOffset) =
    dto
        .ToUniversalTime()
        .ToString("ddd, dd MMM yyyy HH:mm:ss UTC", CultureInfo.InvariantCulture)
