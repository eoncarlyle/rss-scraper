module SourceFeeds

open System
open FsHttp
open DomainModel
open Serialisation
open FSharp.Data
open System.Text.RegularExpressions
open Microsoft.Extensions.Logging

let getSanitisedDiveContent content =
    Regex.Replace(htmlSanitized content, @"(\r?\n){2,}", "\n").Replace("&nbsp;", "")

module Artemis =
    type ArtemisRss = XmlProvider<"Schema/artemis.rss">
    let localArtemisRss = ArtemisRss.Load "Schema/artemis.rss"

    let internal firstSentenceRemove (s: string) =
        if String.IsNullOrEmpty(s) then
            s
        else
            let m = Regex.Match(s, "^[^.]*(?:\.[^.]*)*?\.bm[^.]*\.\s*")
            if m.Success then s.Substring m.Length else s

    let internal deserialiseRssItem (item: ArtemisRss.Item) : RssItem =
        { Title = item.Title
          Guid =
            if isNull (box item.Guid) then
                None
            else
                Some item.Guid.Value
          Link = Some item.Link
          Description = htmlSanitized item.Description |> firstSentenceRemove
          Content = htmlSanitized item.Encoded |> firstSentenceRemove
          PubDate = rfc822Date item.PubDate |> Some }

    let fetchSource (logger: ILogger) url =
        let request =
            http {
                GET url
                CacheControl "no-cache"
                body
            }

        task {
            try
                let response = request |> Request.send
                let body = Response.toText response
                let feed = body |> ArtemisRss.Parse
                return Array.map deserialiseRssItem feed.Channel.Items
            with ex ->
                logger.LogError(ex, "Failed to fetch Artemis source {Url}", url)
                return [||]
        }

module Dive =
    type DiveRss = XmlProvider<"Schema/c-store-dive.rss">
    
    let internal deserialiseRssItem (item: DiveRss.Entry) : RssItem =
        { Title = item.Title
          Guid = if isNull (box item.Id) then None else Some item.Id
          Link = Some item.Link.Href
          Description = ""
          Content = getSanitisedDiveContent item.Content.Value
          PubDate = rfc822Date item.Published |> Some }

    let fetchSource (logger: ILogger) url =
        let request =
            http {
                GET url
                CacheControl "no-cache"
                body
            }

        task {
            try
                let response = request |> Request.send
                let body = Response.toText response
                let feed = body |> DiveRss.Parse
                return Array.map deserialiseRssItem feed.Entries
            with ex ->
                logger.LogError(ex, "Failed to fetch Dive source {Url}", url)
                return [||]
        }
