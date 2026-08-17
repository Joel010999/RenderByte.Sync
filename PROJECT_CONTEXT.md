# RenderByte Sync - Contexto del proyecto

## Objetivo

RenderByte Sync es un agente Windows que lee datos del sistema Alegon desde SQL Server y, en una etapa futura, los sincronizará por HTTPS con una API y PostgreSQL en Railway.

Arquitectura prevista: `Alegon SQL Server -> RenderByte Sync.exe -> HTTPS API -> PostgreSQL en Railway`.

La etapa actual se limita a lectura local y health check. **Todavía no se envían datos a Railway.**

## Regla de seguridad innegociable

Todo acceso a Alegon es exclusivamente de lectura. Nunca se deben ejecutar `INSERT`, `UPDATE`, `DELETE`, `DROP`, `ALTER`, `CREATE` ni otras operaciones que modifiquen la base, su esquema o sus datos.

## Datos confirmados de Alegon

- Base oficial en las tres PCs: `sistema`.
- `movistockdt`: ventas y movimientos (aprox. 2,64 millones de filas).
- `artistock`: stock actual (aprox. 6,8 mil filas).
- `articulo`: productos (aprox. 4,9 mil filas).
- `sisparam`: parámetros de sucursal.
- `locales`: nombres de locales.
- `comppalprf`: tipos de comprobantes.

### Sucursal

```sql
SELECT CONVERT(INT, cont)
FROM sistema.dbo.sisparam
WHERE codi = 'NRO.SUCURS';
```

En la PC relevada, `NRO.SUCURS = 2`, correspondiente a `MOSTRADOR`.

### Stock y productos

- `artistock.saldo` es el stock actual y se filtra por `artistock.depo = NRO.SUCURS`.
- PK de `artistock`: `depo + idarti + bulto`.
- PK de `articulo`: `articulo`.

### Ventas y movimientos

- Los comprobantes comerciales se identifican con `comppalprf.tipo = 'V'`.
- En `movistockdt`, `tipomov = 'VT'` suma venta.
- Otros movimientos, por ejemplo `IN`, pueden cancelar o devolver ventas.
- `cantidad` siempre es positiva; el signo se determina por el tipo de movimiento.
- Identificador lógico: `CLAVEU + ITEM`.
- `fedepo` es fecha y hora de inserción y se usará para lectura incremental.
- Todos los registros analizados tienen `fedepo` y los antiguos no se modifican.
- `costo` y `precio` son los valores reales del movimiento en ese momento.

PK física de `movistockdt`:

`depo, tipomov, fecha, codcom, ptovta, numero, proveedor, idarti, bulto, local, item, CLAVEU`

## Milestone actual

El ejecutable debe comprobar conexión a SQL Server, existencia de `sistema`, sucursal, cantidad de productos, cantidad de stock local y último `movistockdt.fedepo`.

La cadena de conexión se configura fuera del código. No se guardan credenciales en el repositorio.
