using Microsoft.EntityFrameworkCore;
using NexusPort.Infrastructure.Database;
using NexusPort.Modules.Booking.Domain.Entities;

// Called only after the isolated PostgreSQL runner verifies its random marker.
internal static class BookingSaveChecks
{
    public static async Task EnsureSchemaAsync(AppDbContext context)
    {
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS booking_operations (
              id uuid PRIMARY KEY,booking_id uuid NOT NULL REFERENCES bookings(id),operation_type varchar(50) NOT NULL,
              sequence_number integer NOT NULL,expected_gate_direction varchar(20) NOT NULL,status varchar(50) NOT NULL,
              container_id uuid,container_number varchar(20) NOT NULL,container_iso_size varchar(10),cargo_status varchar(20) NOT NULL,
              cargo_type varchar(50) NOT NULL,carrier_booking_number varchar(100),bill_of_lading_number varchar(100),
              delivery_order_number varchar(100),customs_declaration_number varchar(100),seal_number varchar(100),gross_weight_kg numeric(12,2),
              temperature_setpoint numeric(5,2),temperature_unit varchar(10),un_number varchar(20),dg_class varchar(20),
              special_handling_requirements text,verification_status varchar(50) NOT NULL,verified_by uuid,verified_at timestamptz,
              verification_notes text,eir_number varchar(100),eir_issued_at timestamptz,container_condition varchar(100),
              created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now());
            CREATE TABLE IF NOT EXISTS booking_documents (
              id uuid PRIMARY KEY,booking_id uuid NOT NULL REFERENCES bookings(id),booking_operation_id uuid REFERENCES booking_operations(id),
              document_type varchar(80) NOT NULL,document_number varchar(100),file_url varchar(500),file_name varchar(255),
              file_size_bytes bigint,issuer varchar(100),verification_status varchar(50) NOT NULL,verified_by uuid,verified_at timestamptz,
              rejection_reason text,expiry_date timestamptz,created_at timestamptz NOT NULL DEFAULT now(),updated_at timestamptz NOT NULL DEFAULT now());
            """);
    }

    public static async Task<int> RunAsync(AppDbContext context)
    {
        context.ChangeTracker.Clear();
        await EnsureSchemaAsync(context);
        var bookingId=Guid.NewGuid();
        await context.Database.ExecuteSqlRawAsync("INSERT INTO bookings(id) VALUES({0})",bookingId);
        var operation=new BookingOperation(bookingId,"Export","ONEU7524599","GateIn");
        var bookingNote=new BookingDocument(bookingId,"CarrierBookingReference","TEST-BOOKING-NOTE","/test/booking-note.jpg","booking-note.jpg",operation.Id);
        var vgm=new BookingDocument(bookingId,"VgmCertificate","TEST-VGM","/test/vgm.jpg","vgm.jpg",operation.Id);
        operation.Documents.Add(bookingNote);operation.Documents.Add(vgm);
        context.Set<BookingOperation>().Add(operation);
        try {await context.SaveChangesAsync();}
        catch(DbUpdateException error) when(error.InnerException is Npgsql.PostgresException postgres)
        {
            throw new Exception($"Booking save regression: PostgreSQL {postgres.SqlState}, table {postgres.TableName}, column {postgres.ColumnName}.",error);
        }
        var count=0;
        void Check(bool valid,string message){if(!valid)throw new Exception(message);count++;}
        Check(operation.UpdatedAt.HasValue,"new booking operation must receive its database timestamp");
        Check(bookingNote.UpdatedAt.HasValue&&vgm.UpdatedAt.HasValue,"both uploaded documents receive database timestamps");
        context.ChangeTracker.Clear();
        var persisted=await context.Set<BookingOperation>().Include(op=>op.Documents).SingleAsync(op=>op.Id==operation.Id);
        Check(persisted.Documents.Count==2&&persisted.Documents.All(document=>document.UpdatedAt.HasValue),"operation and both required documents are actually saved, not only tracked in memory");
        Check(persisted.Documents.Any(document=>document.DocumentType=="CarrierBookingReference")&&persisted.Documents.Any(document=>document.DocumentType=="VgmCertificate"),"required Booking Note and VGM are preserved");
        persisted.Status="Approved";
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        Check((await context.Set<BookingOperation>().SingleAsync(op=>op.Id==operation.Id)).UpdatedAt.HasValue,"existing operation updates still receive their timestamp");
        Console.WriteLine($"PASS {count} Booking operation/document persistence checks on isolated PostgreSQL.");
        return count;
    }
}
