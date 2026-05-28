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
- Backups y restore desde el plugin (pg_dump/psql).
- Cambio controlado entre PostgreSQL y SQLite.

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

- ⚙️ Configuracion: conexion PostgreSQL, timeout, rutas de binarios y defaults de backup.
- 🔄 Migracion: copia SQLite -> PostgreSQL con progreso en vivo.
- 🛠️ Mantenimiento: VACUUM, REINDEX, backups/restore y tabla de estadisticas.

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

- PostgreSQL Backup: diario a las 02:00 (default).
- PostgreSQL VACUUM ANALYZE: domingo 03:00 (default).
- PostgreSQL REINDEX DATABASE: domingo 04:00 (default).

Recomendacion de operacion:

- Programa REINDEX fuera de horario pico (puede tardar varios minutos).
- Evita solapar backup, vacuum y reindex a la misma hora.

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
