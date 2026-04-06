module Caching

open System
open System.Collections.Generic
open System.Threading.Tasks
open Giraffe.ViewEngine

type CacheValue<'a> =
    { Content: 'a
      ExpireTime: DateTimeOffset }

type Cache() =
    let directCache = Dictionary<string, CacheValue<XmlNode>>()
    let summaryCache = Dictionary<string, CacheValue<XmlNode option>>()

    member this.getOrElseWithDirect(key: string, ifEmptyThunk: Unit -> XmlNode) =
        let current = DateTimeOffset.UtcNow

        match directCache.TryGetValue key with
        | true, entry when entry.ExpireTime > current -> entry.Content
        | _ ->
            let xml = ifEmptyThunk ()

            if directCache.ContainsKey key then
                do directCache.Remove key |> ignore

            directCache.Add(
                key,
                { Content = xml
                  ExpireTime = current.AddMinutes(5.0) }
            )

            xml


    member this.getOrElseWithSummary(key: string, ifEmptyThunk: Unit -> Task<XmlNode option>) =
        let current = DateTimeOffset.UtcNow

        match summaryCache.TryGetValue key with
        | true, entry when entry.ExpireTime > current -> task { return entry.Content }
        | _ ->
            task {
                let! xml = ifEmptyThunk ()

                if summaryCache.ContainsKey key then
                    do summaryCache.Remove key |> ignore

                summaryCache.Add(
                    key,
                    { Content = xml
                      ExpireTime = current.AddMinutes(5.0) }
                )

                return xml
            }
