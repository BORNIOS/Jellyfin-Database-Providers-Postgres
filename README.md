# PostgreSQL para Jellyfin

![Logo del plugin PostgreSQL](Jellyfin.Database.Providers.Postgres/Resources/logo.png)

Usa PostgreSQL como base de datos de Jellyfin sin modificar el core.

[![Jellyfin 10.11.x](https://img.shields.io/badge/Jellyfin-10.11.x-blue?style=flat-square)](https://jellyfin.org)
[![Release](https://img.shields.io/github/v/release/BORNIOS/Jellyfin-Database-Providers-Postgres?style=flat-square)](https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/BORNIOS/Jellyfin-Database-Providers-Postgres/total?style=flat-square&color=00A4DC&label=descargas)](https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/releases)
[![Discord](https://img.shields.io/badge/Discord-Comunidad_Jellyfin-5865F2?style=flat-square&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![Reddit](https://img.shields.io/badge/Reddit-r%2Fjellyfin-FF4500?style=flat-square&logo=reddit&logoColor=white)](https://www.reddit.com/r/jellyfin)

Idioma: [Español](README.md) | [English](README.en.md)

## Que resuelve

- Mejor comportamiento con bibliotecas grandes y concurrencia.
- **Busqueda instantanea** sub-15ms con indice GIN trigram (pg_trgm).
- **Optimizacion automatica** de indices y autovacuum tuning en una sola accion.
- Backups y restore desde el plugin (pg_dump/psql).
- Cambio controlado entre PostgreSQL y SQLite (en ambas direcciones).
- **Exportacion PostgreSQL → SQLite** directo a `.db` sin herramientas externas.

## Compatibilidad

| Componente | Version |
| --- | --- |
| Jellyfin | 10.11.10 |
| PostgreSQL | 13+ (recomendado 16/17) |

## Instalacion

### Opcion A: desde repositorio de plugins (recomendado)

1. En Jellyfin: Admin -> Plugins -> Plugin Repositories.
2. Agrega esta URL:

```text
https://raw.githubusercontent.com/BORNIOS/Jellyfin-Database-Providers-Postgres/main/manifest.json
```

3. Guarda, refresca catalogo e instala PostgreSQL Database Provider.
4. Reinicia Jellyfin.

### Opcion B: manual por ZIP

1. Descarga el ZIP de la ultima release.
2. Extrae en la carpeta de plugins de Jellyfin.
3. Reinicia Jellyfin.

## Vista rapida de la UI del plugin

La pagina del plugin esta separada en 3 pestanas:

- ⚙️ **Configuracion**: conexion PostgreSQL, pool, timeout, rutas de binarios y defaults de backup.
- 🔄 **Migracion**: dos cards:
  - **SQLite → PostgreSQL**: copia jellyfin.db a PostgreSQL con progreso en vivo.
  - **PostgreSQL → SQLite**: exporta todos los datos directamente a un archivo `.db` nativo.
- 🛠️ **Mantenimiento**: VACUUM ANALYZE, REINDEX, **Aplicar optimizaciones** (indices GIN), backups/restore y estadisticas de BD.

## Configuracion recomendada (paso a paso)

### 1) Conexion por formulario (no necesitas escribirla a mano)

La UI construye internamente la cadena con estos campos:

1. Host.
2. Puerto.
3. Base de datos.
4. Usuario.
5. Contrasena.
6. Pool maximo (opciones avanzadas).
7. Command timeout.

Ejemplo de la cadena que se genera:

```text
Host=127.0.0.1;Port=5432;Database=jellyfin;Username=jellyfin;Password=CHANGE_ME;Pooling=true;Maximum Pool Size=200
```

### 2) Valida antes de guardar

Usa Probar conexion.

- Si todo bien: te devuelve Conexion exitosa + version del servidor PostgreSQL.
- Si falla: te devuelve error detallado para corregir host/credenciales/red.

### 3) Guarda configuracion persistente

Al guardar, se persisten:

- Connection string.
- Command timeout.
- Directorio default de backup.
- Compresion ZIP por defecto.
- Ruta de pg_dump (opcional).
- Ruta de psql (opcional).

## Busqueda instantanea (InstantSearch)

A partir de v1.0.0.1 el plugin expone un endpoint de busqueda propio que evita el ORM y consulta directamente con indice GIN trigram (pg_trgm):

- Latencia tipica < 15 ms en bibliotecas medianas/grandes.
- Soporta busqueda por nombre, path y tipo de item.
- Fallback a `ILIKE` si los indices GIN aun no se han creado.
- Se activa al aplicar la optimizacion de rendimiento desde Configuracion.

### Activar indices GIN

1. En el plugin: tab **Mantenimiento** -> **Aplicar optimizaciones**.
2. La accion crea 9 indices GIN CONCURRENTLY y ajusta autovacuum en tablas criticas.
3. El estado de cada indice (creado / pendiente) se muestra en la misma card.
4. La tarea **Optimize GIN Indexes** (semanal, domingo 05:00) mantiene los indices frescos.
5. Mientras no existan indices GIN, la busqueda cae automaticamente en `ILIKE`.

## Exportar PostgreSQL → SQLite

Card disponible en la pestana Migracion, debajo de "Migrar datos de SQLite a PostgreSQL".

**Casos de uso:**
- Revertir Jellyfin a SQLite sin perder datos.
- Copia portable de seguridad de la base en formato nativo.
- Pruebas o inspecciones locales del contenido.

**Como funciona:**
1. El plugin auto-detecta la ruta nativa de `jellyfin.db` (`{DataPath}/jellyfin.db`).
2. Si el archivo `.db` no existe → lo crea con el schema derivado de PostgreSQL.
3. Si el archivo ya existe → respeta las tablas y reemplaza solo el contenido (`DELETE` + `INSERT`).
4. Los datos se escriben en transacciones de 10,000 filas con `PRAGMA journal_mode=WAL`.
5. No requiere herramientas externas (`sqlite3`, `pg_dump`, etc.).

**Pasos:**
1. Ir a Migracion -> **Exportar PostgreSQL → SQLite**.
2. Verificar o cambiar la ruta del `.db` destino.
3. Iniciar exportacion y seguir el progreso en vivo.

## Como migrar de SQLite a PostgreSQL

La migracion de datos no es automatica al instalar.

1. Crea una base vacia en PostgreSQL (ejemplo: jellyfin).
2. En la pestana Configuracion, completa conexion y prueba.
3. Ve a Migracion y confirma ruta de jellyfin.db (se autodetecta si existe).
4. Ajusta batch size (default 1000).
5. Decide si activar Truncar tablas antes de insertar (--truncate).
6. Inicia migracion y espera progreso 100%.
7. Revisa filas copiadas y activa PostgreSQL.

### Recomendacion sobre --truncate en DB grande

- ✅ Recomendado cuando vas a repetir migracion sobre una base PostgreSQL ya poblada.
- ✅ Recomendado en escenarios de reintento para evitar duplicados/inconsistencias por residuos.
- ⚠️ Destructivo sobre datos existentes en destino (hace TRUNCATE + RESTART IDENTITY + CASCADE).

## Como activar PostgreSQL

Desde Configuracion:

1. Verifica que la conexion pase el test.
2. Activa PostgreSQL y reinicia.

Resultado esperado:

- `database.xml` se escribe en modo `PLUGIN_PROVIDER`.
- El provider activo pasa a `Jellyfin.Database.Providers.Postgres.dll`.

## Como volver a SQLite (rollback)

Desde Configuracion:

1. Revertir a SQLite y reiniciar.
2. El plugin elimina `database.xml`.

Cuando conviene rollback:

- Caida de conectividad a PostgreSQL.
- Mantenimiento urgente.
- Migracion incompleta o validacion funcional fallida.

## Backups y restore

### Donde configurar rutas de binarios

En Configuracion:

1. Ruta de pg_dump (opcional).
2. Ruta de psql (opcional).

Si no defines rutas, el plugin intenta resolverlos desde PATH del sistema.

### Backup manual

En Mantenimiento -> Crear backup ahora.

- Genera .sql y opcionalmente .zip segun configuracion.
- Usa directorio persistido de backups.

### Restore manual

En Mantenimiento -> Restablecer backup.

- Acepta .sql o .zip (con .sql dentro).
- Por seguridad operativa, restaura reemplazando objetos existentes en schema public.

## Estadisticas de base de datos (tabla de stats)

En Mantenimiento -> Actualizar estadisticas.

Muestra:

- Tamano total de la base.
- Conexiones activas.
- Tabla por tabla: filas estimadas, tamano total, tamano de tabla, tamano de indices.

Usa esta vista para detectar:

- Tablas con crecimiento anormal.
- Bloat de indices (indices creciendo mucho vs tabla).
- Momentos adecuados para VACUUM/REINDEX.

## Tareas de mantenimiento del plugin

Tambien aparecen en Tareas programadas de Jellyfin:

| Tarea | Default | Descripcion |
|---|---|---|
| PostgreSQL Backup | Diario 02:00 | Backup con pg_dump |
| PostgreSQL VACUUM ANALYZE | Domingo 03:00 | Libera espacio y actualiza estadisticas |
| PostgreSQL REINDEX DATABASE | Domingo 04:00 | Reconstruye todos los indices |
| Optimize GIN Indexes | Domingo 05:00 | Mantiene los indices GIN trigram (v1.0.0.1) |

Recomendacion de operacion:

- Programa REINDEX fuera de horario pico (puede tardar varios minutos).
- Evita solapar backup, vacuum y reindex a la misma hora.
- Si la BD tiene escrituras intensas, ejecuta Optimize GIN Indexes antes del pico de uso semanal.

## Recomendacion sobre optimizacion de DB en Jellyfin

Si ya operas con PostgreSQL activo, prioriza las tareas de mantenimiento de este plugin (VACUUM/REINDEX/Backup).

Si tienes tareas heredadas de optimizacion pensadas para SQLite, revisalas para evitar trabajo redundante durante ventanas criticas.

## Checklist post-migracion

1. Login correcto con usuarios existentes.
2. Actividad y progreso de reproduccion actualizando.
3. Conteos clave validados (UserData, Users, TypedBaseItems).
4. Sin errores de conexion PostgreSQL en logs.
5. Backup manual probado al menos una vez.

## FAQ corta

P: Instale el plugin y no migro datos automaticamente.

R: Es esperado. Debes ejecutar migracion y luego activar PostgreSQL.

P: Puedo editar `database.xml` manualmente.

R: Si, pero se recomienda hacerlo desde la UI del plugin para evitar errores.

P: La migracion falla o da resultados raros en reintento.

R: En DB grande o migraciones repetidas, habilita --truncate para reiniciar tablas destino antes de insertar.

## Comunidad

¿Dudas o feedback? Puedes abrir un issue o unirte a la comunidad Jellyfin:

[![Discord](https://img.shields.io/badge/Discord-Unete_a_la_comunidad-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![Reddit](https://img.shields.io/badge/Reddit-r%2Fjellyfin-FF4500?style=for-the-badge&logo=reddit&logoColor=white)](https://www.reddit.com/r/jellyfin)
