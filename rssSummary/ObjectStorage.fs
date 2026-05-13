module ObjectStorage

open Amazon.S3
open Amazon.S3.Model
open System
open System.IO
open System.Threading.Tasks
open DomainModel

type ObjectStorageService(s3Client: IAmazonS3, bucketName: string) =

    member _.GetObject(key: string) : Task<S3Object option> =
        task {
            try
                let request = GetObjectRequest(BucketName = bucketName, Key = key)
                let! response = s3Client.GetObjectAsync request
                use reader = new StreamReader(response.ResponseStream)
                let! content = reader.ReadToEndAsync()

                return
                    Some
                        { Content = content
                          ETag = response.ETag }
            with :? NoSuchKeyException ->
                return None
        }

    member _.PutObject (key: string) (content: string) (maybeETag: string option) =
        task {
            try
                let request =
                    PutObjectRequest(
                        BucketName = bucketName,
                        Key = key,
                        ContentBody = content,
                        DisablePayloadSigning = true
                    )

                match maybeETag with
                | Some eTag -> request.IfMatch <- eTag
                | None -> request.IfNoneMatch <- "*"

                let! update = s3Client.PutObjectAsync request
                return Ok update.ETag
            with :? AmazonS3Exception as ex ->
                return Error ex.StatusCode
        }

    member _.ObjectExists(key: string) =
        task {
            try
                let request = GetObjectMetadataRequest(BucketName = bucketName, Key = key)
                let! _ = s3Client.GetObjectMetadataAsync(request)
                return true
            with :? AmazonS3Exception as ex when ex.StatusCode = System.Net.HttpStatusCode.NotFound ->
                return false
        }

    member _.RetryHttp (times: int) (factory: Unit -> Task<Result<'a, Net.HttpStatusCode>>) =
        let mutable result = Error(Net.HttpStatusCode.BadRequest)

        task {
            for _ in 1..times do
                if result.IsError then
                    let! nextResult = factory ()
                    result <- nextResult

            return result
        }
