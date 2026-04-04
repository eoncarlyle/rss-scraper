module SourceFeeds

open System
open FsHttp // Did not know you could scope this way, which is nice
open DomainModel
open Serialisation
open FSharp.Data
open System.Text.RegularExpressions
open System.IO

let sourceSettings =
    File.ReadAllText "source-settings.json" |> deserialise<SourceSettings>

let getSanitisedDiveContent content =
    Regex.Replace(htmlSanitized content, @"(\r?\n){2,}", "\n").Replace("&nbsp;", "")

module Artemis =
    type ArtemisRss = XmlProvider<"schema/artemis.rss">
    let localArtemisRss = ArtemisRss.Load "schema/artemis.rss"

    let deserialiseRssItem (item: ArtemisRss.Item) : RssItem =
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

    let fetchSource url =
        let request =
            http {
                GET url
                CacheControl "no-cache"
                body
            }

        task {
            let response = request |> Request.send
            let body = Response.toText response
            let feed = body |> ArtemisRss.Parse
            return Array.map deserialiseRssItem feed.Channel.Items
        }

module Dive =
    type DiveRss = XmlProvider<"schema/c-store-dive.rss">

    let deserialiseRssItem (item: DiveRss.Entry) : RssItem =
        { Title = item.Title
          Guid = if isNull (box item.Id) then None else Some item.Id
          Link = Some item.Link.Href
          Description = ""
          Content = getSanitisedDiveContent item.Content.Value
          PubDate = rfc822Date item.Published |> Some }

    let fetchSource url =
        let request =
            http {
                GET url
                CacheControl "no-cache"
                body
            }

        task {
            let response = request |> Request.send
            let body = Response.toText response
            let feed = body |> DiveRss.Parse
            return Array.map deserialiseRssItem feed.Entries
        }
