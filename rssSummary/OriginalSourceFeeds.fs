module OriginalSourceFeeds

open FsHttp // Did not know you could scope this way, which is nice
open DomainModels
open Serialisation
open FSharp.Data

module Artemis =

    type ArtemisRss = XmlProvider<"schema/artemis.rss">

    let localArtemisRss = ArtemisRss.Load "schema/artemis.rss"

    let deserialiseRssItem (item: ArtemisRss.Item) : MinimalRssItem =
        { Title = item.Title
          Guid = if isNull (box item.Guid) then None else Some item.Guid.Value
          Link = Some item.Link
          Description = htmlSanitized item.Description |> firstSentenceRemove
          Content = htmlSanitized item.Encoded |> firstSentenceRemove
          PubDate = Some item.PubDate }

    let localArtemisItems =
        localArtemisRss.Channel.Items |> Array.map deserialiseRssItem

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
