using Npgsql;

static class SourceRepairFixtures
{
    public static Guid Id(int number) => Guid.Parse($"03810000-0000-4000-8000-{number:000000000000}");

    public static async Task PrepareLegacy(NpgsqlConnection pg)
    {
        await using (var command = new NpgsqlCommand("SELECT to_regclass('public.export_bookings') IS NOT NULL", pg))
            if ((bool)(await command.ExecuteScalarAsync())!)
                await DraftFixtures.Sql(pg, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "source-repair.sql")));
        await RegistrationFixtures.Prepare(pg);
        await DraftFixtures.Sql(pg, """
            DELETE FROM export_booking_containers WHERE id::text LIKE '03810000-%';
            DELETE FROM export_bookings WHERE id::text LIKE '03810000-%';
            DELETE FROM export_voyages WHERE id::text LIKE '03810000-%';
            DELETE FROM container_declarations WHERE id::text LIKE '03810000-%';
            DELETE FROM containers WHERE id::text LIKE '03810000-%';
            """);
        // Only called after Program has checked the dedicated localhost target
        // and matching custom marker. Simulate the exact reported schema drift.
        await DraftFixtures.Sql(pg, """
            ALTER TABLE export_bookings ADD COLUMN IF NOT EXISTS created_by uuid REFERENCES users(id);
            ALTER TABLE export_bookings ADD COLUMN IF NOT EXISTS shipping_line_id varchar NOT NULL DEFAULT 'legacy-line';
            ALTER TABLE export_bookings ADD COLUMN IF NOT EXISTS shipper_customer_id varchar;
            ALTER TABLE export_bookings ADD COLUMN IF NOT EXISTS shipper_name text;
            ALTER TABLE export_bookings DROP COLUMN IF EXISTS carrier_id;
            ALTER TABLE export_bookings DROP COLUMN IF EXISTS line_user_id;
            ALTER TABLE export_bookings DROP COLUMN IF EXISTS request_id;
            ALTER TABLE export_bookings DROP COLUMN IF EXISTS request_hash;
            """);
        await DraftFixtures.Sql(pg, """
            UPDATE export_bookings SET created_by=@a,shipper_customer_id='legacy-unmapped-customer',
                shipper_name='Legacy customer name is not an authorization grant' WHERE id=@b;
            """, DraftFixtures.Id(1), DraftFixtures.Id(201));
    }

    public static async Task<string> LegacySnapshot(NpgsqlConnection pg)
    {
        await using var command = new NpgsqlCommand("""
            SELECT COALESCE(jsonb_agg(to_jsonb(b)-'carrier_id'-'line_user_id'-'request_id'-'request_hash' ORDER BY b.id),'[]'::jsonb)::text
            FROM export_bookings b WHERE b.id::text LIKE '03610000-%'
            """, pg);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    public static async Task InsertOwnedSources(NpgsqlConnection pg)
    {
        await DraftFixtures.Sql(pg, """
            INSERT INTO export_voyages(id,vessel_name,voyage_number,receive_from,cy_cutoff,vgm_cutoff,status)
            VALUES(@a,'SOURCE REPAIR DEMO VESSEL','REPAIR-001',now()-interval '1 day',now()+interval '10 days',now()+interval '9 days','Scheduled')
            ON CONFLICT(id) DO NOTHING;
            """, Id(701));
        foreach (var (booking, company, creator) in new[] { (201,101,1),(202,102,2) })
            await DraftFixtures.Sql(pg, """
                INSERT INTO export_bookings(id,booking_no,carrier_id,line_user_id,request_id,request_hash,voyage_id,status,port_status,
                    created_by,shipping_line_id,shipper_customer_id,shipper_name)
                VALUES(@a,@b,@c,@d,@e,'explicit-checker-request',@f,'ContainersDeclared','Accepted',@g,@h,'not-used-as-company-proof','Source repair sample')
                ON CONFLICT(id) DO NOTHING;
                """, Id(booking), "REPAIR-BOOKING-" + booking, DraftFixtures.Id(company), DraftFixtures.Id(7), Id(booking + 100), Id(701),
                DraftFixtures.Id(creator), DraftFixtures.Id(7).ToString());
        foreach (var (number, company, booking, type, cargo) in new[]
        {
            (1,101,201,801,"general"), (2,101,201,802,"reefer"), (3,102,202,801,"general"),
        })
        {
            var containerNumber = new[] { "NXPU9990011", "NXPU9990027", "NXPU9990032" }[number - 1];
            await DraftFixtures.Sql(pg, """
                INSERT INTO containers(id,container_no,container_type_id,status,legacy_secret)
                VALUES(@a,@b,@c,'expected','SAMPLE-MASTER-NOT-AUTHORIZATION') ON CONFLICT(id) DO NOTHING;
                """, Id(500 + number), containerNumber, DraftFixtures.Id(type));
            await DraftFixtures.Sql(pg, """
                INSERT INTO container_declarations(id,container_id,carrier_id,load_status,cargo_type,seal_number,gross_weight_kg,required_power_kw,customer_name)
                VALUES(@a,@b,@c,'full',@d,'REPAIR-SEAL',21000,@e,'Source repair test') ON CONFLICT(id) DO NOTHING;
                """, Id(600 + number), Id(500 + number), DraftFixtures.Id(company), cargo, cargo == "reefer" ? 5m : (decimal?)null);
            await DraftFixtures.Sql(pg, """
                INSERT INTO export_booking_containers(id,export_booking_id,container_id,declaration_id,container_no,type_code,seal_number,vgm_kg,cargo_type,
                    target_temperature,status,active,customs_cleared)
                VALUES(@a,@b,@c,@d,@e,@f,'REPAIR-SEAL',22000,@g,@h,'Expected',true,true) ON CONFLICT(id) DO NOTHING;
                """, Id(300 + number), Id(booking), Id(500 + number), Id(600 + number), containerNumber, cargo == "reefer" ? "22R1" : "22G1", cargo,
                cargo == "reefer" ? -18m : (decimal?)null);
        }
    }
}
