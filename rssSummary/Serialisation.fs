module Serialisation

open System
open System.Text.Json
open System.Text.Json.Serialization
open System.Text.RegularExpressions
open DomainModel

let jsonOptions =
    let options =
        JsonSerializerOptions(PropertyNamingPolicy = JsonNamingPolicy.CamelCase)

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

let serializeDerivedFeed (feed: DerivedFeed) : string =
    JsonSerializer.Serialize(feed, jsonOptions)

let deserializeDerivedFeed (json: string) : DerivedFeed =
    JsonSerializer.Deserialize<DerivedFeed>(json, jsonOptions)

let serializeSourceSettings (settings: SourceSettings) : string =
    JsonSerializer.Serialize(settings, jsonOptions)

let deserializeSourceSettings (json: string) : SourceSettings =
    JsonSerializer.Deserialize<SourceSettings>(json, jsonOptions)

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
