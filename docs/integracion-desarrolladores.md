# Integración de plugins con PG Provider

PG Provider ofrece a los plugins de Jellyfin almacenamiento PostgreSQL privado, administrado y observable, sin exponer la cadena de conexión ni el esquema `public` de Jellyfin.

![Pestaña Integraciones](../Screenshots/Tab-Intergations.png)

La pestaña **🧩 Integraciones** permite al administrador conocer qué plugins usan el proveedor, revisar su espacio y sus tablas, consultar una muestra acotada de datos y decidir qué mantenimiento programado recibe cada esquema.

## Modelo de aislamiento

Cada plugin conserva sus propios datos en un esquema con nombre legible y sufijo GUID, por ejemplo `miplugin_0123456789abcdef0123456789abcdef`. El registro interno del proveedor vincula de forma permanente el GUID del plugin, su nombre y su esquema.

El contrato no expone:

- La cadena de conexión ni objetos `NpgsqlConnection`.
- El esquema `public`, que pertenece a Jellyfin.
- Datos, esquemas o políticas de otros plugins.

Los plugins deben obtener los datos de Jellyfin mediante los contratos normales de Jellyfin; PG Provider es únicamente el almacén para el estado que el propio plugin posee.

> Los plugins cargados son código confiable dentro del mismo proceso. Este aislamiento previene acceso accidental entre integraciones; no sustituye una frontera de seguridad frente a código malicioso.

## Descubrir el proveedor

La capacidad pública se identifica como `jellyfin.postgres.private-schema-host`, versión de protocolo `1`. El contrato es `Jellyfin.Database.Providers.Postgres.Api.IPluginSchemaHost`.

Un plugin con referencia de compilación al ensamblado del proveedor puede solicitar `IPluginSchemaHost` mediante el contenedor de servicios de Jellyfin. Si quiere que PostgreSQL sea opcional, debe tratar su ausencia como una señal para usar su propio almacenamiento alternativo.

```csharp
var host = serviceProvider.GetService<IPluginSchemaHost>();
if (host is null)
{
    // PG Provider no está instalado o no está activo: usar el fallback del plugin.
    return;
}
```

### Contextos de carga de plugins

Jellyfin puede cargar cada plugin en un contexto de ensamblado distinto. Por ello, dos tipos que se llaman exactamente `IPluginSchemaHost` pueden no ser asignables entre sí si provienen de copias distintas de la DLL. En ese caso, `GetService<IPluginSchemaHost>()` desde una referencia privada del consumidor puede devolver `null`, aunque PG Provider esté activo y otro plugin ya lo esté usando.

Para que PostgreSQL sea opcional y la detección sea robusta, sigue este orden:

1. Consulta `IPluginManager.Plugins` y busca primero la instancia viva de PG Provider, no una DLL encontrada en disco ni una copia cualquiera de los ensamblados cargados.
2. En el ensamblado de esa instancia, localiza `PostgresSchemaHostCapability`, verifica `ProtocolName` y una versión de protocolo compatible, y obtiene el tipo indicado por `ContractTypeName`.
3. Solicita ese **tipo procedente del ensamblado vivo** al `IServiceProvider` no genérico: `services.GetService(activeContractType)`.
4. Si se obtiene el host, invócalo mediante un adaptador de reflexión limitado al contrato público. Si no se obtiene, registra el motivo técnico y usa el fallback del plugin.

El diagnóstico debe distinguir `PG Provider no instalado o inactivo`, `contrato no encontrado en el ensamblado vivo` y `el contenedor no resolvió el tipo del contrato activo`. No lo informes genéricamente como “el proveedor no se registró”: ese mensaje oculta los problemas de identidad de tipos y hace que reintentar parezca una solución.

No crees `PostgresPluginSchemaHost`, ni ninguna implementación del proveedor, con `ActivatorUtilities`, reflexión o constructores públicos. La implementación es deliberadamente interna y el host registrado por Jellyfin tiene su propio ciclo de vida, configuración y garantías de aislamiento. Crear otra instancia acopla el consumidor a detalles no contractuales y puede ignorar el estado del proveedor. La reflexión, cuando sea imprescindible, debe limitarse al marcador y a `IPluginSchemaHost` públicos del ensamblado vivo.

El mejor camino a largo plazo para integraciones que necesitan consumo fuertemente tipado es un ensamblado de abstracciones estable, compartido por proveedor y consumidores. Hasta que exista, la resolución mediante el contrato del ensamblado vivo evita duplicar las clases públicas del proveedor dentro de cada plugin.

## Abrir un esquema y aplicar migraciones

Usa siempre el GUID inmutable del manifiesto de tu plugin y un nombre estable en minúsculas. El nombre debe tener menos de 64 caracteres, comenzar con una letra y terminar con el GUID sin guiones.

```csharp
var pluginId = Guid.Parse("01234567-89ab-cdef-0123-456789abcdef");
var schema = await host.OpenSchemaAsync(
    new PluginSchemaRequest(pluginId, "miplugin_0123456789abcdef0123456789abcdef"), cancellationToken);

await host.ApplyMigrationsAsync(schema,
[
    new PluginSchemaMigration(
        "001_create_state",
        "CREATE TABLE state (id uuid PRIMARY KEY, value jsonb NOT NULL, updated_at timestamptz NOT NULL DEFAULT now())"),
    new PluginSchemaMigration(
        "002_state_updated_at_index",
        "CREATE INDEX ix_state_updated_at ON state (updated_at DESC)")
], cancellationToken);
```

Las migraciones son transaccionales, se serializan por esquema y guardan un checksum. Una migración ya aplicada es inmutable: para cambiarla, agrega una migración nueva. Para una integración anterior al registro existe `MigratePreRegistrySchemaAsync`, que solo puede reclamar el esquema predecesor exacto y lo renombra atómicamente.

## Datos parametrizados

Toda operación usa una sentencia sin calificar dentro del esquema privado asignado. Parametriza siempre los valores.

```csharp
await host.ExecuteAsync(
    schema,
    "INSERT INTO state (id, value) VALUES (@id, @value) ON CONFLICT (id) DO UPDATE SET value = excluded.value, updated_at = now()",
    [
        new PluginSqlParameter("id", entityId),
        new PluginSqlParameter("value", payload)
    ],
    cancellationToken);

var found = await host.QueryAsync(
    schema,
    "SELECT value FROM state WHERE id = @id",
    reader => reader.Read() ? reader.GetString(0) : null,
    [new PluginSqlParameter("id", entityId)],
    cancellationToken);
```

## Límites deliberados

El proveedor rechaza sentencias múltiples, comentarios SQL y referencias calificadas a otros esquemas. También bloquea `public`, catálogos PostgreSQL, `COPY`, `VACUUM`, `ANALYZE`, gestión de roles, extensiones, permisos y cambios de esquema.

Esto significa que:

- El plugin crea sus tablas e índices mediante migraciones, no con una conexión directa.
- El plugin no ejecuta mantenimiento ni backups por sí mismo.
- Las optimizaciones del núcleo de Jellyfin —`pg_trgm`, GIN e interceptores de consultas— no se aplican automáticamente a tablas privadas. Diseña índices según tus consultas y mide su efecto.
- La exportación PostgreSQL → SQLite recupera los datos de Jellyfin; cada plugin debe definir su propia estrategia de fallback o exportación para sus datos privados.

## Integraciones y mantenimiento

El administrador controla las tareas desde **🧩 Integraciones**:

- El backup completo PostgreSQL incluye siempre todos los esquemas privados registrados.
- `ANALYZE` programado está habilitado inicialmente para actualizar estadísticas del planificador.
- `VACUUM (ANALYZE)` y `REINDEX CONCURRENTLY` empiezan deshabilitados y solo se ejecutan para los esquemas seleccionados por el administrador.
- Las acciones manuales están acotadas al esquema elegido: `ANALYZE` por esquema y `VACUUM (ANALYZE)` o `REINDEX` por tabla.

Las políticas son administrativas y no forman parte del contrato que consume el plugin. Una integración no puede activarse mantenimiento, alterar la política de otra ni inspeccionar esquemas ajenos.

## Buenas prácticas

- Conserva un fallback funcional si PostgreSQL es opcional para tu plugin.
- Mantén migraciones pequeñas, ordenadas e inmutables.
- Usa índices solo para patrones de consulta demostrables; evita índices especulativos.
- No incluyas credenciales, datos de usuarios ni consultas con valores sensibles en logs o issues.
- Prueba migraciones y consultas contra PostgreSQL real en una base temporal antes de publicar.

## Colabora

Las mejoras al contrato público deben discutirse antes en un issue para preservar compatibilidad. Son especialmente bienvenidas pruebas de integración, mejoras de observabilidad, documentación y casos reales de plugins que usen un esquema privado de forma segura.

Consulta también la [guía en inglés](developer-integration.en.md).
