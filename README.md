# Lab SNAT - App de prueba

App .NET 8 minima para validar el comportamiento SNAT en Azure App Service.

## Endpoints

| Endpoint | Descripcion |
|----------|-------------|
| `GET /` | Health check - verifica que la app esta corriendo |
| `GET /storage` | Escribe un registro en Azure Table Storage y muestra si uso IP privada o publica |
| `GET /redis` | Lee y escribe en Redis Cache y muestra latencia |
| `GET /full` | Simula un timbrado completo: Redis + Storage y muestra riesgo SNAT |

## App Settings requeridos

| Variable | Descripcion |
|----------|-------------|
| `StorageConnectionString` | Connection string de la Storage Account |
| `RedisConnectionString` | Connection string del Redis Cache |

## Como interpretar los resultados

### Sin VNet (App Service original)
```json
{
  "snat_risk": "ALTO - Storage usa IP publica (consume puertos SNAT)",
  "storage": {
    "resolved_ip": "52.x.x.x",
    "is_private": false,
    "duration_ms": 4500
  }
}
```

### Con VNet + Private Endpoints (App Service clon)
```json
{
  "snat_risk": "BAJO - Storage usa red privada",
  "storage": {
    "resolved_ip": "10.10.2.x",
    "is_private": true,
    "duration_ms": 45
  }
}
```

La diferencia en `duration_ms` y `is_private` confirma que los Private Endpoints funcionan.
