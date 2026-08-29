<div align="center">

![Logo del plugin PostgreSQL](Jellyfin.Database.Providers.Postgres/Resources/logo.png)

🌐 &nbsp;**Español**&nbsp; · &nbsp;[**English**](README.en.md)

<br>

# 🐘 PostgreSQL Database Provider

**Plugin para Jellyfin** que reemplaza SQLite por PostgreSQL como motor de base de datos,
sin modificar el núcleo de Jellyfin. Migración bidireccional, búsqueda casi instantánea,
health check automático, mantenimiento programado e integración opcional con [JellyTrend](https://github.com/BORNIOS/JellyTrend).

<br>

[![Last Commit](https://img.shields.io/github/last-commit/BORNIOS/Jellyfin-Database-Providers-Postgres?style=flat-square&color=00A4DC&label=último%20commit)](https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/commits/main)
[![CI Build](https://img.shields.io/github/actions/workflow/status/BORNIOS/Jellyfin-Database-Providers-Postgres/build.yaml?style=flat-square&color=00A4DC&label=CI)](https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/actions)
[![Jellyfin](https://img.shields.io/badge/Jellyfin-10.11.x-00A4DC?style=flat-square&logo=jellyfin&logoColor=white)](https://jellyfin.org)
[![Release](https://img.shields.io/github/v/release/BORNIOS/Jellyfin-Database-Providers-Postgres?style=flat-square&color=00A4DC)](https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/BORNIOS/Jellyfin-Database-Providers-Postgres/total?style=flat-square&color=00A4DC&label=descargas)](https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/releases)
[![Discord](https://img.shields.io/badge/Discord-Comunidad_Jellyfin-5865F2?style=flat-square&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![License](https://img.shields.io/github/license/BORNIOS/Jellyfin-Database-Providers-Postgres?style=flat-square&color=555)](LICENSE)

</div>

---

## ✨ Características

- 🐘 **PostgreSQL como backend nativo** — EF Core + Npgsql, sin tocar el core de Jellyfin.
- 🔍 **Búsqueda casi instantánea** (< 15 ms típico) con índice GIN trigram (`pg_trgm`); fallback automático a `ILIKE`.
- 🔄 **Migración bidireccional** — SQLite → PostgreSQL y PostgreSQL → SQLite sin herramientas externas.
- 🩺 **Health Check automático** al arrancar: diagnostica bloat, índices inválidos, secuencias desfasadas y queries lentas.
- 🛡️ **Prevención de errores** — interceptores EF Core para upserts, logging de errores DB y normalización de `DateTime.Kind`.
- 🔧 **Optimización automática** — índices GIN CONCURRENTLY, tuning de autovacuum en tablas críticas.
- 💾 **Backups programados** con `pg_dump`, compresión ZIP opcional y restore desde la UI.
- ⚡ **Integración opcional con [JellyTrend](https://github.com/BORNIOS/JellyTrend)** — recomendaciones 4-10× más rápidas con SQL nativo optimizado.
- 📊 **Estadísticas de BD** — tamaño, conexiones activas y análisis tabla por tabla desde la UI.
- 🔁 **Rollback a SQLite** en un clic desde la configuración.

---

## 🖥️ Interfaz del plugin

El plugin añade una página de configuración con **cuatro pestañas**:

### ⚙️ Configuración

Gestiona la conexión, el pool de conexiones y las rutas de herramientas externas.

![Tab Configuración](Screenshots/Tab-Configurations.png)

| Parámetro | Descripción | Default |
|---|---|---|
| Connection string | Cadena completa de Npgsql | — |
| Schema | Schema PostgreSQL a usar | `public` |
| Command timeout | Tiempo máximo de una query EF Core (segundos) | `60` |
| Min pool size | Conexiones mínimas activas en el pool | `4` |
| Max pool size | Conexiones máximas en el pool | `100` |
| Max auto-prepare | Statements preparados en servidor (0 = desactivado) | `50` |
| PgBin path | Ruta a `pg_dump` / `psql` / `pg_restore` | auto-detect |
| Backup directory | Carpeta donde se guardan los backups | — |
| Backup compression | Comprimir backup en ZIP | `true` |

**Flujo de configuración recomendado:**
1. Rellena los campos de conexión y pulsa **Probar conexión** — devuelve la versión del servidor si todo es correcto.
2. Ajusta pool y timeout según tu carga.
3. Pulsa **Guardar configuración**.
4. Una vez migrados los datos, pulsa **Activar PostgreSQL** y reinicia.

---

### 🩺 Health Check

Diagnóstico automático de la base de datos. Se ejecuta **10 segundos después del arranque** y puede lanzarse manualmente desde la UI.

![Tab Health Check](Screenshots/Tab-Health.png)

Comprueba y reporta:

| Check | Descripción |
|---|---|
| **Conexión** | Verifica que PostgreSQL es accesible |
| **Extensiones** | `pg_trgm`, `pg_stat_statements` disponibles |
| **Índices inválidos** | Detecta y puede auto-reparar |
| **Bloat de tablas** | Tablas con > 20 % de tuplas muertas |
| **Secuencias desfasadas** | Secuencias fuera de rango respecto a los datos |
| **Estadísticas obsoletas** | Tablas sin `ANALYZE` reciente |
| **Queries lentas** | Top queries por tiempo acumulado (vía `pg_stat_statements`) |

Los resultados se clasifican con semáforo: `Info` / `Warn` / `Error`. Los problemas marcados como reparables pueden corregirse en un clic desde la misma card.

---

### 🔄 Migración

Dos operaciones disponibles con progreso en vivo:

![Tab Migración](Screenshots/Tab-Migrations.png)

#### SQLite → PostgreSQL

Copia `jellyfin.db` a PostgreSQL tabla por tabla.

1. La ruta de `jellyfin.db` se autodetecta (`{DataPath}/jellyfin.db`).
2. Ajusta el **batch size** (default 1000).
3. Activa **Truncar tablas antes de insertar** si repites la migración sobre datos existentes.
4. Pulsa **Iniciar migración** y sigue el progreso.
5. Al llegar al 100 %, activa PostgreSQL desde la pestaña Configuración.

> ⚠️ La opción `--truncate` hace `TRUNCATE + RESTART IDENTITY + CASCADE` en el destino. Úsala en reintentos para evitar duplicados; es destructiva con datos existentes.

#### PostgreSQL → SQLite

Exporta toda la base directamente a un archivo `.db` nativo sin herramientas externas.

- Si el `.db` no existe → lo crea con el schema derivado de PostgreSQL.
- Si el `.db` ya existe → preserva las tablas y reemplaza solo el contenido (`DELETE` + `INSERT`).
- Escritura en transacciones de 10 000 filas con `PRAGMA journal_mode=WAL`.

**Casos de uso:** revertir a SQLite, copia portable de seguridad, inspección local.

---

### 🛠️ Mantenimiento

Operaciones de mantenimiento, backups y estadísticas de la base de datos.

![Tab Mantenimiento](Screenshots/Tab-Maintenance.png)

| Acción | Descripción |
|---|---|
| **VACUUM ANALYZE** | Libera espacio y actualiza estadísticas del planificador |
| **REINDEX DATABASE** | Reconstruye todos los índices |
| **Aplicar optimizaciones** | Crea 9 índices GIN CONCURRENTLY + tuning de autovacuum |
| **Crear backup ahora** | Genera `.sql` (y `.zip` si está activado) con `pg_dump` |
| **Restablecer backup** | Restaura desde `.sql` o `.zip` |
| **Actualizar estadísticas** | Muestra tamaño total, conexiones activas y métricas por tabla |

> 💡 El botón **Aplicar optimizaciones** activa también la búsqueda casi instantánea si `pg_trgm` está disponible.
| **Crear backup ahora** | Genera `.sql` (y `.zip` opcional) con `pg_dump` |
| **Restablecer backup** | Restaura desde `.sql` o `.zip` |
| **Actualizar estadísticas** | Tamaño total, conexiones activas y métricas por tabla |

---

## 🔍 Búsqueda casi instantánea

Cuando los índices GIN están activos, el plugin expone un endpoint propio que evita EF Core:

- Latencia típica **< 15 ms** en bibliotecas medianas y grandes (depende del hardware y del cliente).
- Búsqueda por nombre, path y tipo de ítem.
- **Fallback automático** a `ILIKE` si los índices GIN aún no existen.
- Se activa en **Mantenimiento → Aplicar optimizaciones**.

---

## 🔁 Activar / Desactivar PostgreSQL

### Activar PostgreSQL

1. Configura y prueba la conexión en la pestaña **Configuración**.
2. Migra los datos (**Migración → SQLite → PostgreSQL**).
3. Pulsa **Activar PostgreSQL** y confirma el reinicio.

Resultado: `database.xml` se escribe en modo `PLUGIN_PROVIDER` apuntando a este plugin.

### Volver a SQLite (rollback)

1. En **Configuración** pulsa **Desactivar / Volver a SQLite**.
2. El plugin elimina `database.xml`.
3. Reinicia Jellyfin.

Cuándo hacer rollback: caída de conectividad a PostgreSQL, mantenimiento urgente o migración incompleta.

---

## 💾 Backups

### Configurar

En **Configuración**:
- **PgBin path** — ruta a `pg_dump`/`psql`/`pg_restore`. Déjalo vacío para usar el PATH del sistema.
- **Backup directory** — carpeta destino de los archivos.
- **Backup compression** — activa ZIP del `.sql`.

### Manual

En **Mantenimiento**: **Crear backup ahora** / **Restablecer backup** (acepta `.sql` o `.zip`).

---

## 📅 Tareas programadas

| Tarea | Default | Descripción |
|---|---|---|
| **PostgreSQL Backup** | Diario 02:00 | Backup con `pg_dump` |
| **PostgreSQL VACUUM ANALYZE** | Domingo 03:00 | Libera espacio y actualiza estadísticas |
| **PostgreSQL REINDEX DATABASE** | Domingo 04:00 | Reconstruye todos los índices |
| **Optimize GIN Indexes** | Domingo 05:00 | Mantiene los índices GIN trigram frescos |

> ⚠️ Evita solapar REINDEX, VACUUM y Backup en la misma ventana horaria.

---

## ⚡ Integración con JellyTrend

[![JellyTrend](https://img.shields.io/badge/JellyTrend-Plugin%20compatible-00A4DC?style=flat-square&logo=jellyfin&logoColor=white)](https://github.com/BORNIOS/JellyTrend)

[**JellyTrend**](https://github.com/BORNIOS/JellyTrend) es el plugin oficial de tendencias y recomendaciones para Jellyfin — sincroniza el trending de TMDB con tu biblioteca y genera un carrusel estilo Netflix en la pantalla de inicio.

Cuando ambos plugins están instalados, el motor de recomendaciones de [JellyTrend](https://github.com/BORNIOS/JellyTrend) detecta automáticamente este plugin y sustituye las queries de `ILibraryManager` por SQL nativo optimizado:

| Motor | Comportamiento |
|---|---|
| **SQLite** (sin este plugin) | `ILibraryManager` — compatible con cualquier instalación |
| **PostgreSQL** (con este plugin) | SQL directo con operador `&&` de arrays + índices GIN — **4-10× más rápido** |

> ✅ Sin configuración manual. Si ambos plugins están instalados, la integración se activa sola.

---

## ✅ Checklist post-migración

1. Login correcto con los usuarios existentes.
2. Progreso de reproducción actualizando correctamente.
3. Conteos clave validados (UserData, Users, BaseItems).
4. Sin errores de conexión PostgreSQL en logs de Jellyfin.
5. Health Check ejecutado sin errores (`Warn` aceptable, `Error` requiere acción).
6. Backup manual probado al menos una vez.
7. Índices GIN aplicados (**Mantenimiento → Aplicar optimizaciones**).

---

## ❓ FAQ

<details>
<summary><b>Instalé el plugin pero no migró datos automáticamente.</b></summary>

Es el comportamiento esperado. Ejecuta la migración desde **Migración → SQLite → PostgreSQL** y luego activa PostgreSQL desde **Configuración**.
</details>

<details>
<summary><b>La migración falla o da resultados raros en reintentos.</b></summary>

Activa **Truncar tablas antes de insertar** para reiniciar las tablas destino antes de insertar. Es destructivo sobre datos existentes en PostgreSQL, pero garantiza un resultado limpio.
</details>

<details>
<summary><b>¿Puedo editar database.xml manualmente?</b></summary>

Sí, pero se recomienda usar la UI del plugin para evitar errores de formato.
</details>

<details>
<summary><b>¿Qué pasa si exporto a SQLite y el .db ya existe?</b></summary>

El plugin preserva la estructura de tablas y reemplaza solo los datos (DELETE + INSERT). El archivo no se elimina ni recrea.
</details>

<details>
<summary><b>¿La búsqueda rápida requiere cambios en el cliente Jellyfin?</b></summary>

No. Es un endpoint de servidor. La UI del plugin lo usa internamente cuando PostgreSQL está activo y los índices GIN existen.
</details>

<details>
<summary><b>¿Por qué los timestamps muestran UTC en lugar de mi zona horaria?</b></summary>

En versiones anteriores se usaba el switch global `Npgsql.EnableLegacyTimestampBehavior` que forzaba UTC en todas las lecturas. Desde v2.0.0.0 esto se reemplazó por el interceptor `DateTimeKindNormalizingInterceptor`, que solo normaliza parámetros de escritura con `Kind=Unspecified`, respetando la timezone del servidor PostgreSQL en las lecturas.
</details>

---

## 🤝 Comunidad

¿Dudas, sugerencias o encontraste un bug? Abre un [issue](https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/issues) o únete a la comunidad oficial de Jellyfin:

[![Discord](https://img.shields.io/badge/Discord-Únete_a_la_comunidad-5865F2?style=for-the-badge&logo=discord&logoColor=white)](https://discord.jellyfin.org)
[![Reddit](https://img.shields.io/badge/Reddit-r%2Fjellyfin-FF4500?style=for-the-badge&logo=reddit&logoColor=white)](https://www.reddit.com/r/jellyfin)

---

<div align="center">

Hecho con ❤️ para la comunidad Jellyfin &nbsp;·&nbsp; [⭐ Star en GitHub](https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres)

</div>
