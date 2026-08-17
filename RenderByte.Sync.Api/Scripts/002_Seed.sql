INSERT INTO organizations (id, slug, name) 
VALUES (1, 'prueba', 'La Casa de Prueba')
ON CONFLICT DO NOTHING;

-- Example API KEY HASH (SHA-256) for testing purposes
INSERT INTO sources (organization_id, source_id, branch_id, name, api_key_hash)
VALUES (1, 'TEST-SUCURSAL-2', 2, 'Sucursal Test', '6be5b50df6df9939e8024fa8eb9ffc5d80482b6be0f9b69bfa7ed123491fa30a')
ON CONFLICT DO NOTHING;
