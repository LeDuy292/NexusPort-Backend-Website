using Npgsql;

static class DraftFixtures
{
    public const string Secret = "service-draft-isolated-fixture-secret-at-least-32-characters";
    public static Guid Id(int value) => Guid.Parse($"03610000-0000-4000-8000-{value:000000000000}");
    public static Guid Service(int value) => Guid.Parse($"03500000-0000-4000-8000-{value:000000000000}");

    public static async Task Prepare(NpgsqlConnection pg)
    {
        await Sql(pg, """
            CREATE TABLE IF NOT EXISTS users(id uuid PRIMARY KEY,role text NOT NULL,is_active boolean NOT NULL DEFAULT true);
            CREATE TABLE IF NOT EXISTS carriers(id uuid PRIMARY KEY,company_name text NOT NULL);
            CREATE TABLE IF NOT EXISTS carrier_users(user_id uuid NOT NULL REFERENCES users(id),carrier_id uuid NOT NULL REFERENCES carriers(id),PRIMARY KEY(user_id,carrier_id));
            CREATE TABLE IF NOT EXISTS nexusport_schema_versions(version text PRIMARY KEY,description text NOT NULL);
            CREATE TABLE IF NOT EXISTS container_types(id uuid PRIMARY KEY,code text NOT NULL,size text,category text);
            CREATE TABLE IF NOT EXISTS containers(id uuid PRIMARY KEY,container_no text NOT NULL,container_type_id uuid REFERENCES container_types(id),legacy_secret text);
            CREATE TABLE IF NOT EXISTS container_declarations(id uuid PRIMARY KEY,container_id uuid REFERENCES containers(id),carrier_id uuid REFERENCES carriers(id),load_status text,cargo_type text,seal_number text,gross_weight_kg numeric,required_power_kw numeric,customer_name text);
            CREATE TABLE IF NOT EXISTS container_visits(id uuid PRIMARY KEY,container_id uuid REFERENCES containers(id),declaration_id uuid REFERENCES container_declarations(id),visit_status text,load_status text,cargo_type text,seal_number text,pickup_code text,visit_reference text);
            CREATE TABLE IF NOT EXISTS container_visit_access(container_visit_id uuid REFERENCES container_visits(id),carrier_id uuid REFERENCES carriers(id),PRIMARY KEY(container_visit_id,carrier_id));
            CREATE TABLE IF NOT EXISTS export_voyages(id uuid PRIMARY KEY,vessel_name text,voyage_number text,receive_from timestamptz,cy_cutoff timestamptz,vgm_cutoff timestamptz,status text);
            CREATE TABLE IF NOT EXISTS export_bookings(id uuid PRIMARY KEY,booking_no text,carrier_id uuid REFERENCES carriers(id),voyage_id uuid REFERENCES export_voyages(id),status text,port_status text,created_at timestamptz DEFAULT now());
            CREATE TABLE IF NOT EXISTS export_booking_containers(id uuid PRIMARY KEY,export_booking_id uuid REFERENCES export_bookings(id),container_id uuid REFERENCES containers(id),declaration_id uuid REFERENCES container_declarations(id),container_no text,type_code text,seal_number text,vgm_kg numeric,cargo_type text,target_temperature numeric,un_number text,dg_class text,status text,active boolean);
            """);
        for (var pass = 0; pass < 2; pass++)
            foreach (var filename in new[] { "catalog.sql", "drafts.sql", "registrations.sql" })
                await Sql(pg, await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, filename)));

        foreach (var (id, role, active) in new[]
        {
            (1, "Transport Company", true), (2, "Transport Company", true), (3, "Carrier Staff", true),
            (4, "Administrator", true), (5, "Transport Company", false), (6, "Transport Company", true), (7, "Shipping Line", true),
        }) await Sql(pg, "INSERT INTO users(id,role,is_active) VALUES(@a,@b,@c) ON CONFLICT(id) DO UPDATE SET role=excluded.role,is_active=excluded.is_active", Id(id), role, active);
        foreach (var id in new[] { 101, 102 })
            await Sql(pg, "INSERT INTO carriers(id,company_name) VALUES(@a,@b) ON CONFLICT(id) DO NOTHING", Id(id), "Draft test company " + id);
        foreach (var (user, carrier) in new[] { (1,101),(2,102),(3,101),(5,101),(6,101),(6,102) })
            await Sql(pg, "INSERT INTO carrier_users(user_id,carrier_id) VALUES(@a,@b) ON CONFLICT DO NOTHING", Id(user), Id(carrier));
        await Sql(pg, "INSERT INTO container_types(id,code,size,category) VALUES(@a,'22G1','20FT','Dry'),(@b,'22R1','20FT','Reefer') ON CONFLICT DO NOTHING", Id(801), Id(802));
        var rows = new[]
        {
            (501,601,101,"DRYU1000001","full","general",801),
            (502,602,102,"OTHR1000002","full","general",801),
            (503,603,101,"EMPT1000003","empty","general",801),
            (504,604,101,"REEF1000004","full","reefer",802),
            (505,605,101,"IMPA1000005","full","general",801),
            (506,606,102,"SHRD1000006","full","general",801),
            (507,607,102,"PRVT1000007","full","general",801),
            (508,608,101,"EMPI1000008","empty","general",801),
        };
        foreach (var (container, declaration, carrier, number, load, cargo, type) in rows)
        {
            await Sql(pg, "INSERT INTO containers(id,container_no,container_type_id,legacy_secret) VALUES(@a,@b,@c,'PRIVATE_MASTER_SECRET') ON CONFLICT DO NOTHING", Id(container), number, Id(type));
            await Sql(pg, "INSERT INTO container_declarations(id,container_id,carrier_id,load_status,cargo_type,seal_number,gross_weight_kg,required_power_kw,customer_name) VALUES(@a,@b,@c,@d,@e,@f,22000,5,@g) ON CONFLICT DO NOTHING", Id(declaration), Id(container), Id(carrier), load, cargo, carrier == 102 ? "FOREIGN_SEAL_SECRET" : "SEAL-A", carrier == 102 ? "FOREIGN_CUSTOMER_SECRET" : "Draft customer A");
        }
        await Sql(pg, "INSERT INTO export_voyages(id,vessel_name,voyage_number,receive_from,cy_cutoff,vgm_cutoff,status) VALUES(@a,'TEST NEXUS VOYAGER','TEST-V001',now()-interval '1 day',now()+interval '10 days',now()+interval '9 days','Scheduled') ON CONFLICT DO NOTHING", Id(701));
        foreach (var (booking, carrier, status) in new[] { (201,101,"ContainersDeclared"),(202,102,"ContainersDeclared"),(203,101,"Cancelled") })
            await Sql(pg, "INSERT INTO export_bookings(id,booking_no,carrier_id,voyage_id,status,port_status) VALUES(@a,@b,@c,@d,@e,'Accepted') ON CONFLICT DO NOTHING", Id(booking), "TEST-EXPORT-" + booking, Id(carrier), Id(701), status);
        foreach (var (occurrence, booking, container, declaration, number, type, cargo, temperature) in new[]
        {
            (301,201,501,601,"DRYU1000001","22G1","general",(decimal?)null),
            (302,202,502,602,"OTHR1000002","22G1","general",(decimal?)null),
            (303,201,503,603,"EMPT1000003","22G1","general",(decimal?)null),
            (304,201,504,604,"REEF1000004","22R1","reefer",(decimal?)-18),
        }) await Sql(pg, "INSERT INTO export_booking_containers(id,export_booking_id,container_id,declaration_id,container_no,type_code,seal_number,vgm_kg,cargo_type,target_temperature,status,active) VALUES(@a,@b,@c,@d,@e,@f,'SEAL-A',24000,@g,@h,'Expected',true) ON CONFLICT DO NOTHING", Id(occurrence), Id(booking), Id(container), Id(declaration), number, type, cargo, temperature);
        foreach (var (visit, container, declaration, load) in new[] { (401,505,605,"full"),(402,507,607,"full"),(403,506,606,"full"),(404,508,608,"empty") })
            await Sql(pg, "INSERT INTO container_visits(id,container_id,declaration_id,visit_status,load_status,cargo_type,seal_number,pickup_code,visit_reference) VALUES(@a,@b,@c,'active',@d,'general',@e,'TC-PRIVATE-PICKUP-SECRET','VISIT_PRIVATE_SECRET') ON CONFLICT DO NOTHING", Id(visit), Id(container), Id(declaration), load, visit == 403 ? "FOREIGN_SEAL_SECRET" : "SEAL-A");
        await Sql(pg, "INSERT INTO container_visit_access(container_visit_id,carrier_id) VALUES(@a,@b) ON CONFLICT DO NOTHING", Id(403), Id(101));
    }

    public static async Task<int> Sql(NpgsqlConnection pg, string sql, params object?[] args)
    {
        await using var command = new NpgsqlCommand(sql, pg);
        for (var index = 0; index < args.Length; index++) command.Parameters.AddWithValue(((char)('a' + index)).ToString(), args[index] ?? DBNull.Value);
        return await command.ExecuteNonQueryAsync();
    }

    public static async Task<string> Fingerprint(NpgsqlConnection pg)
    {
        var values = new List<string>();
        foreach (var table in new[] { "containers", "container_declarations", "container_visits", "container_visit_access", "export_bookings", "export_booking_containers", "export_voyages" })
        {
            await using var command = new NpgsqlCommand($"SELECT md5(COALESCE(string_agg(row_to_json(t)::text,'|' ORDER BY row_to_json(t)::text),'')) FROM {table} t", pg);
            values.Add((string)(await command.ExecuteScalarAsync())!);
        }
        return string.Join(":", values);
    }
}
