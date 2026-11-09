using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NexusPort.Infrastructure.ExternalServices;

var count=0;
void Check(bool valid,string message){if(!valid)throw new Exception(message);count++;}
var config=new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
    ["AWS:BucketName"]="test-bucket",["AWS:Region"]="us-east-1"
}).Build();
using var client=new RecordingS3();
var recorder=client;
var service=new S3StorageService(config,NullLogger<S3StorageService>.Instance,client);
var image=new byte[] {1,2,3,4};
var first=await service.UploadBytesAsync(image,"same-name.JPG","image/jpeg","vehicles");
var second=await service.UploadBytesAsync(image,"same-name.JPG","image/jpeg","vehicles");
Check(recorder.Uploads.Count==2,"even without explicit static configuration, uploads use the configured S3 credential provider instead of local disk");
Check(new Uri(first).Host.Contains("s3")&&Uri.UnescapeDataString(new Uri(first).AbsolutePath).Contains("NexusPort/vehicles/")&&!first.Contains("localhost"),"vehicle upload returns S3 URL");
Check(first!=second,"equal filenames never overwrite another fleet image");
Check(recorder.Uploads.All(upload=>upload.Bucket=="test-bucket"&&upload.Key.EndsWith(".jpg")&&upload.ContentType=="image/jpeg"&&upload.Size==image.Length),"upload preserves configured bucket, type, bytes and extension");
var renewed=service.GetPresignedUrl("https://test-bucket.s3.amazonaws.com/NexusPort/vehicle%20photo.jpg?X-Amz-Signature=expired");
Check(Uri.UnescapeDataString(new Uri(renewed).AbsolutePath)=="/NexusPort/vehicle photo.jpg"&&!renewed.Contains("Signature=expired"),"expired signatures are renewed using decoded original S3 key");
var pathStyle=service.GetPresignedUrl("https://s3.us-east-1.amazonaws.com/test-bucket/NexusPort/photo.png?X-Amz-Signature=expired");
Check(Uri.UnescapeDataString(new Uri(pathStyle).AbsolutePath)=="/NexusPort/photo.png","path-style S3 reference does not duplicate bucket in its object key");
Check(service.GetPresignedUrl("/uploads/vehicles/existing.jpg")=="/uploads/vehicles/existing.jpg","historical local images are not disguised as S3 uploads");
var calls=recorder.Uploads.Count;
recorder.FailUploads=true;
try{await service.UploadBytesAsync(image,"fail.jpg","image/jpeg","vehicles");throw new Exception("S3 error was hidden");}
catch(AmazonS3Exception){Check(recorder.Uploads.Count==calls,"S3 failure propagates; no local fallback or false success URL");}
Console.WriteLine($"PASS {count} S3 upload checks; mocked S3 client only, no database, files or live AWS requests.");

sealed class RecordingS3:AmazonS3Client
{
    public List<(string Bucket,string Key,string ContentType,long Size)> Uploads {get;}=new();
    public bool FailUploads {get;set;}
    public RecordingS3():base("test-access","test-secret",RegionEndpoint.USEast1){}
    public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request,CancellationToken token=default)
    {
        if(FailUploads)throw new AmazonS3Exception("Simulated S3 denial");
        Uploads.Add((request.BucketName,request.Key,request.ContentType,request.InputStream.Length));
        return Task.FromResult(new PutObjectResponse {HttpStatusCode=System.Net.HttpStatusCode.OK});
    }
}
