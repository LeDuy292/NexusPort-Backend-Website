using Npgsql;

static class RegistrationFixtures
{
    public static Guid Id(int value) => Guid.Parse($"03710000-0000-4000-8000-{value:000000000000}");
    public static async Task Prepare(NpgsqlConnection pg)
    {
        await DraftFixtures.Prepare(pg);
        // Extend only the isolated checker schema with authoritative source columns
        // that Part 2 intentionally did not need. No real environment is accepted.
        await DraftFixtures.Sql(pg, """
            ALTER TABLE containers ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT 'expected';
            ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS started_at timestamptz;
            ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS completed_at timestamptz;
            ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS valid_to timestamptz;
            ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS data_status text;
            ALTER TABLE container_visits ADD COLUMN IF NOT EXISTS movement_type text;
            ALTER TABLE export_booking_containers ADD COLUMN IF NOT EXISTS customs_cleared boolean NOT NULL DEFAULT false;
            ALTER TABLE export_bookings ADD COLUMN IF NOT EXISTS version integer NOT NULL DEFAULT 1;
            ALTER TABLE export_voyages ADD COLUMN IF NOT EXISTS version integer NOT NULL DEFAULT 1;
            """);
        for (var pass = 0; pass < 2; pass++)
            await DraftFixtures.Sql(pg, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "registrations.sql")));
        await DraftFixtures.Sql(pg, "INSERT INTO users(id,role,is_active) VALUES(@a,'Carrier',true) ON CONFLICT(id) DO UPDATE SET role='Carrier',is_active=true", DraftFixtures.Id(8));
        await DraftFixtures.Sql(pg, "INSERT INTO carrier_users(user_id,carrier_id) VALUES(@a,@b) ON CONFLICT DO NOTHING", DraftFixtures.Id(8), DraftFixtures.Id(101));
        await DraftFixtures.Sql(pg, """
            UPDATE containers SET status='expected';
            UPDATE container_types SET size='20FT';
            UPDATE container_visits SET started_at=now()-interval '2 days',completed_at=NULL,
                valid_to=now()+interval '8 days',data_status='verified',movement_type='vessel_discharge';
            UPDATE export_booking_containers SET customs_cleared=false;
            UPDATE export_voyages SET receive_from=now()-interval '1 day',cy_cutoff=now()+interval '10 days',
                vgm_cutoff=now()+interval '9 days',status='Scheduled';
            """);
        foreach (var (id, service, size, equipment, amount) in new[]
        {
            (901,1,"ALL","ALL",90000), (902,1,"20FT","ALL",200000),
            (903,1,"ALL","DRY",100000), (904,1,"20FT","DRY",300000),
            (905,1,"20FT","REEFER",500000),
            (906,2,"ALL","ALL",100000), (907,3,"ALL","ALL",1100),
            (908,4,"ALL","ALL",20000), (909,5,"ALL","ALL",7000),
        }) await DraftFixtures.Sql(pg, """
            INSERT INTO eport_tariffs(id,service_id,size,equipment,amount,unit,effective_from,note,created_by)
            SELECT @a,id,@c,@d,@e,unit,current_date-2,'Isolated registration fixture; not an operational port tariff',@f
            FROM eport_services WHERE id=@b
            ON CONFLICT(id) DO UPDATE SET amount=excluded.amount,is_active=true,effective_from=excluded.effective_from,effective_to=NULL
            """, Id(id), DraftFixtures.Service(service), size, equipment, amount, DraftFixtures.Id(4));
    }
}
