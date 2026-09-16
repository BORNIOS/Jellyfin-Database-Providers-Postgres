# Plan de publicación: 2.0.1 y 3.0.0

Cómo publicar las dos versiones que sostienen la migración a Jellyfin 12:
**2.0.1** (para Jellyfin 10.11.x) y **3.0.0** (para Jellyfin 12.1.x).

> English version: [`release-plan.en.md`](release-plan.en.md)

---

## 1. Por qué hay que publicar 2.0.1 antes que 3.0.0

El catálogo de Jellyfin **sirve un único `manifest.json`**: el de la rama `main`. Cada
versión del manifiesto declara su `targetAbi`, y el cliente descarga la versión **más alta
compatible** con el servidor del usuario.

Consecuencias directas:

1. Un usuario en **Jellyfin 10.11.x** sólo puede ver e instalar entradas con
   `targetAbi 10.11.*`. Si 2.0.1 no está en el manifiesto, ese usuario se queda en 2.0.0.0.
2. Ese usuario **necesita 2.0.1** para exportar su PostgreSQL a SQLite sin toparse con el
   error de clave primaria por mayúsculas/minúsculas (ver
   [`migracion-10.11-a-12.md`](migracion-10.11-a-12.md), sección 2).
3. Sin export no hay puente hacia Jellyfin 12: son **dos releases con propósitos distintos**,
   no dos intentos de la misma cosa.

```
v2.0.1  → rescata a los usuarios de 10.11 (export fiable a SQLite)
v3.0.0  → habilita Jellyfin 12.1 con el nuevo contrato de proveedores
```

---

## 2. Qué se corrigió en el flujo de release

`release.yml` actualizaba el `manifest.json` **de la rama desde la que se lanzaba**. Al
publicar desde `release/2.0.1`, la entrada quedaba en esa rama y **nunca llegaba al catálogo**
(que se lee de `main`): el release existía, pero nadie lo veía.

Ahora el flujo, tras crear la entrada en la rama de release, **replica la misma entrada en
`main`** mediante un worktree temporal y un commit del bot. La mejora está aplicada en:

- rama `release/V3` (publica 3.0.0 con `targetAbi 12.1.0.0`), y
- rama `release/2.0.1` (publica 2.0.1 con `targetAbi 10.11.0.0`).

El paso es idempotente: si `main` ya tiene esa versión, termina sin commit.

---

## 3. Antes de publicar

```bash
# 1. Ambas ramas compilan sin advertencias
git switch release/2.0.1   && dotnet build Jellyfin.Database.Providers.Postgres.sln -c Release
git switch release/V3      && dotnet build Jellyfin.Database.Providers.Postgres.sln -c Release

# 2. La suite de 3.0.0 pasa en verde (68 pruebas)
$env:POSTGRES_TEST_CONNECTION = 'Host=localhost;Port=5432;Database=postgres;Username=postgres;Password=***'
./Tools/Generar-ResumenEjecutivo.ps1
```

Comprueba además que `main` y `release/V3` están alineadas en lo que respecta al plugin
(el manifiesto se actualiza solo, no lo edites a mano).

---

## 4. Publicar 2.0.1 (primero)

**GitHub → Actions → 📦 Release Plugin → Run workflow**

| Entrada | Valor |
|---|---|
| `release_version` | `2.0.1` |
| `changelog` | ver más abajo |
| `prerelease` | `false` |
| Rama | `release/2.0.1` |

Changelog sugerido:

```
2.0.1 — Fix crítico de exportación PostgreSQL → SQLite

- Fix: las columnas que forman la clave primaria se copian sin normalizar, por lo que
  los pares de filas que solo difieren en mayúsculas/minúsculas ya no colapsan en la
  misma clave y el export completo deja de abortar.
- Fix: cuando hay una violación de constraint, el log indica tabla, número de fila y
  valores de la clave en lugar del error criptico de SQLite.
- CI: la entrada del catálogo se publica también en main para que Jellyfin 10.11.x
  pueda ver esta version.

Requiere Jellyfin 10.11.x. Para Jellyfin 12.1 usa la version 3.0.0.
```

Resultado: tag `v2.0.1`, ZIP `Jellyfin.Database.Providers.Postgres-2.0.1.0.zip`, entrada
`2.0.1.0` con `targetAbi 10.11.0.0` en el manifiesto de `main`.

---

## 5. Publicar 3.0.0 (después)

**GitHub → Actions → 📦 Release Plugin → Run workflow**

| Entrada | Valor |
|---|---|
| `release_version` | `3.0.0` |
| `changelog` | ver más abajo |
| `prerelease` | `false` |
| Rama | `release/V3` (o `main` tras fusionarla) |

Changelog sugerido:

```
3.0.0 — Jellyfin 12.1

- Nuevo: adaptacion completa al contrato de proveedores de Jellyfin 12.1 (requiere .NET 10).
- Fix critico: PurgeDatabase implementado. En 2.x lanzaba NotImplementedException y rompia
  la restauracion del respaldo completo del sistema.
- Nuevo: consola SQL de solo lectura en el panel, con 10 plantillas de diagnostico e historial.
- Nuevo: opciones avanzadas funcionales (tamano de pool, sentencias preparadas, timeout),
  validadas y visibles en el estado actual.
- Nuevo: suite de pruebas xUnit (68 pruebas) con resumen ejecutivo autogenerado en CI.
- Mejor: log propio del plugin con detalle estructurado de PostgresException.
- Mejor: exportacion a SQLite mas robusta y arranque seguro en servidores ya migrados.

Requiere Jellyfin 12.1.x. Para Jellyfin 10.11.x usa la version 2.0.1.
```

Resultado: tag `v3.0.0`, ZIP `Jellyfin.Database.Providers.Postgres-3.0.0.0.zip`, entrada
`3.0.0.0` con `targetAbi 12.1.0.0` en el manifiesto de `main`.

---

## 6. Verificación posterior (obligatoria)

```bash
# 6.a El manifiesto servido contiene las dos entradas y sus ABI
curl -s https://raw.githubusercontent.com/BORNIOS/Jellyfin-Database-Providers-Postgres/main/manifest.json \
  | jq '.[0].versions[] | {version, targetAbi}'

# Esperado:
# { "version": "2.0.1.0", "targetAbi": "10.11.0.0" }
# { "version": "3.0.0.0", "targetAbi": "12.1.0.0" }

# 6.b El checksum del manifiesto coincide con el ZIP descargado
curl -sL -o pgp-2.0.1.0.zip https://github.com/BORNIOS/Jellyfin-Database-Providers-Postgres/releases/download/v2.0.1/Jellyfin.Database.Providers.Postgres-2.0.1.0.zip
md5sum pgp-2.0.1.0.zip     # debe coincidir con el campo checksum de la entrada

# 6.c El ZIP trae los tres DLLs
unzip -l pgp-2.0.1.0.zip   # Jellyfin.Database.Providers.Postgres.dll, Npgsql.dll, Npgsql.EntityFrameworkCore.PostgreSQL.dll
```

Prueba funcional mínima, en dos instalaciones limpias:

1. **Jellyfin 10.11.11** → el catálogo ofrece 2.0.1; instálalo y ejecuta un export a SQLite.
2. **Jellyfin 12.1.x** → el catálogo ofrece 3.0.0; instálalo, importa ese SQLite y activa
   PostgreSQL.

---

## 7. Riesgos y notas

- **No reutilices una versión ya publicada**: el flujo sustituye la entrada, pero el tag y el
  ZIP del release anterior quedan huérfanos. Si necesitas corregir algo, publica 2.0.2 / 3.0.1.
- **Usa `prerelease: true`** si quieres validar el ZIP antes de exponerlo; el manifiesto se
  actualiza igual, así que retíralo del manifiesto si decides no publicarlo.
- **Los ZIP no son intercambiables**: el de 2.0.1 lleva DLLs `net9.0` y el de 3.0.0 DLLs
  `net10.0`.
- **El bot necesita permiso de escritura** (`permissions: contents: write`, ya declarado) para
  crear el release y empujar el manifiesto.

## 8. Vuelta atrás

1. Borra el release y el tag en GitHub.
2. Revierte el commit del bot en `main` que añadió la entrada al manifiesto:
   `git revert <sha> -- manifest.json` y `git push origin main`.
3. En el servidor, desinstala el plugin y vuelve a la versión anterior.

---

## 9. Sigue leyendo

- [Manual de migración de 10.11.x a 12.x](migracion-10.11-a-12.md)
- [Novedades de 3.0.0 frente a 2.0.1](novedades-3.0.0.md)
