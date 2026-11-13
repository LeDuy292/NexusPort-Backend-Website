using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NexusPort.Infrastructure.Database;

namespace NexusPort.Modules.ServiceCatalog.Application;

public sealed class ServiceMailWorker(IServiceScopeFactory scopes,ILogger<ServiceMailWorker> logger):BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while(!stoppingToken.IsCancellationRequested){
            var processed = false;
            try{
                await using var scope=scopes.CreateAsyncScope();
                processed = await scope.ServiceProvider.GetRequiredService<ServiceMailQueue>().SendNext(stoppingToken);
            }
            catch(OperationCanceledException) when(stoppingToken.IsCancellationRequested){break;}
            catch(Exception error){logger.LogWarning("Service mail queue unavailable ({ErrorType}); will retry.",error.GetType().Name);}
            if (!processed)
            {
                try{await Task.Delay(TimeSpan.FromSeconds(2),stoppingToken);}catch(OperationCanceledException){break;}
            }
        }
    }
}
public sealed class ServiceMailQueue(AppDbContext db,IServiceWorkflowEmail email)
{
    public async Task<bool> SendNext(CancellationToken ct)
    {
        var connection=(NpgsqlConnection)db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var ready=new NpgsqlCommand("SELECT to_regclass('eport_service_mail_queue') IS NOT NULL",connection);
        if(await ready.ExecuteScalarAsync(ct) is not true)return false;
        await using(var expired=new NpgsqlCommand("UPDATE eport_service_mail_queue SET status='FAILED',last_error='Retry limit reached after an interrupted send.' WHERE status='SENDING' AND attempts>=10 AND available_at<=now()",connection))await expired.ExecuteNonQueryAsync(ct);
        var lease=Guid.NewGuid();JsonObject? job=null;
        await using(var claim=new NpgsqlCommand("""
            UPDATE eport_service_mail_queue q SET status='SENDING',lease_id=@lease,attempts=attempts+1,available_at=now()+interval '2 minutes'
            WHERE id=(SELECT id FROM eport_service_mail_queue WHERE status IN ('PENDING','SENDING') AND available_at<=now() AND attempts<10
                ORDER BY available_at,id FOR UPDATE SKIP LOCKED LIMIT 1)
            RETURNING row_to_json(q)::text
            """,connection)){
            claim.Parameters.AddWithValue("lease",lease);
            if(await claim.ExecuteScalarAsync(ct) is string json)job=JsonNode.Parse(json)!.AsObject();
        }
        if(job==null)return false;
        var sent=false;
        try{
            var payload=job["payload"]!;
            var recipient = job["recipient"]!.GetValue<string>();
            var subject = payload["subject"]!.GetValue<string>();
            var body = payload["body"]!.GetValue<string>();
            var isHtml = payload["isHtml"]?.GetValue<bool>() ?? true;
            var attachmentName = payload["attachmentName"]?.GetValue<string>();
            byte[]? attachmentBytes = null;
            var mimeType = payload["mimeType"]?.GetValue<string>() ?? "application/pdf";

            if (payload["attachmentPdfBase64"] is JsonValue base64Val && base64Val.TryGetValue<string>(out var base64Str) && !string.IsNullOrEmpty(base64Str))
            {
                attachmentBytes = Convert.FromBase64String(base64Str);
                mimeType = "application/pdf";
            }
            else if (payload["attachmentHtml"] is JsonValue htmlVal && htmlVal.TryGetValue<string>(out var htmlStr) && !string.IsNullOrEmpty(htmlStr))
            {
                attachmentBytes = System.Text.Encoding.UTF8.GetBytes(htmlStr);
                mimeType = "text/html";
            }
            await email.SendMessage(recipient, subject, body, attachmentName ?? "Chung-tu.pdf", attachmentBytes, mimeType, isHtml, ct);
            sent=true;
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){throw;}
        catch(Exception){/* Keep payment/approval committed; no message contents or credentials in logs. */}
        await using var finish=new NpgsqlCommand("""
            UPDATE eport_service_mail_queue SET status=CASE WHEN @sent THEN 'SENT' WHEN attempts>=10 THEN 'FAILED' ELSE 'PENDING' END,
              sent_at=CASE WHEN @sent THEN now() ELSE NULL END,available_at=now()+least(attempts*60,1800)*interval '1 second',
              last_error=CASE WHEN @sent THEN NULL ELSE 'SMTP delivery failed; check EmailSettings and recipient.' END
            WHERE id=@id AND lease_id=@lease AND status='SENDING'
            """,connection);
        finish.Parameters.AddWithValue("sent",sent);finish.Parameters.AddWithValue("id",Guid.Parse(job["id"]!.GetValue<string>()));finish.Parameters.AddWithValue("lease",lease);
        await finish.ExecuteNonQueryAsync(ct);return sent;
    }
}
