module OriginalSourceFeeds

open FsHttp // Did not know you could scope this way, which is nice
open DomainModels
open Serialisation
open FSharp.Data
open System.Text.RegularExpressions

let getSanitisedDiveContent content =
    Regex.Replace(htmlSanitized content, @"(\r?\n){2,}", "\n").Replace("&nbsp;", "")

module Artemis =
    type ArtemisRss = XmlProvider<"schema/artemis.rss">
    let localArtemisRss = ArtemisRss.Load "schema/artemis.rss"

    let deserialiseRssItem (item: ArtemisRss.Item) : MinimalRssItem =
        { Title = item.Title
          Guid =
            if isNull (box item.Guid) then
                None
            else
                Some item.Guid.Value
          Link = Some item.Link
          Description = htmlSanitized item.Description |> firstSentenceRemove
          Content = htmlSanitized item.Encoded |> firstSentenceRemove
          PubDate = Some item.PubDate }

    let fetchSource url =
        let request =
            http {
                GET url
                CacheControl "no-cache"
                body
            }

        task {
            let! response = request |> Request.sendTAsync
            let body = Response.toText response
            let feed = body |> ArtemisRss.Parse
            return Array.map deserialiseRssItem feed.Channel.Items
        }

module GroceryDive =
    type GroceryDiveRss = XmlProvider<"schema/grocery-dive.rss">
    let localGroceryDiveRss = GroceryDiveRss.Load "schema/grocery-dive.rss"

    let deserialiseRssItem (item: GroceryDiveRss.Entry) : MinimalRssItem =
        { Title = item.Title
          Guid = if isNull (box item.Id) then None else Some item.Id
          Link = Some item.Link.Href
          Description = ""
          Content = getSanitisedDiveContent item.Content.Value
          PubDate = Some item.Published }

    let localArtemisItems = localGroceryDiveRss.Entries

    let fetchSource url =
        let request =
            http {
                GET url
                CacheControl "no-cache"
                body
            }

        task {
            let! response = request |> Request.sendTAsync
            let body = Response.toText response
            let feed = body |> GroceryDiveRss.Parse
            return Array.map deserialiseRssItem feed.Entries
        }
