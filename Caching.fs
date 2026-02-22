module Caching

open System
open Microsoft.Extensions.Caching.Memory
open System.Collections.Generic
open Giraffe.ViewEngine

type XmlCacheValue =
    { Xml: XmlNode
      ExpireTime: DateTimeOffset }

type Cache = Dictionary<string, XmlCacheValue>

let getCache () = Dictionary<string, XmlCacheValue>()

let getOrElseWith key (ifEmptyThunk: Unit -> XmlNode) (cache: Cache) =
    let current = DateTimeOffset.UtcNow

    match cache.TryGetValue key with
    | true, entry when entry.ExpireTime > current -> entry.Xml
    | _ ->
        let xml = ifEmptyThunk ()

        cache.Add(
            key,
            { Xml = xml
              ExpireTime = current.AddMinutes(5.0) }
        )

        xml
