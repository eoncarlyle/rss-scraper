module OriginalSourceFeeds

open FsHttp // Did not know you could scope this way, which is nice
open Models
open Serialisation
open FSharp.Data

module Artemis =

    type ArtemisRss = XmlProvider<"schema/artemis.rss">

    let localArtemisRss = ArtemisRss.Load "schema/artemis.rss"

    let deserialiseRssItem (item: ArtemisRss.Item) : MinimalRssItem =
        { Title = item.Title
          Guid = Some item.Guid.Value
          Link = Some item.Link
          Description = sanitized item.Description
          Content = sanitized item.Encoded
          PubDate = Some item.PubDate }

    let localArtemisItems =
        localArtemisRss.Channel.Items |> Array.map deserialiseRssItem

    let getRssItems url =
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
