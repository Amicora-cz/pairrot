-- Feature 1: proposed PostgreSQL schema for a fresh local database.
-- Apply through the project's migration workflow during implementation.
-- UUIDs, UTC timestamps, secret generation, and authorization belong to the application.
-- This file does not create tenants or contain credentials.

BEGIN;

CREATE TABLE tenants (
    id uuid PRIMARY KEY,
    parent_tenant_id uuid REFERENCES tenants (id) ON DELETE RESTRICT,
    external_id varchar(200) NOT NULL,
    name varchar(200) NOT NULL,
    contact_email varchar(254),
    status varchar(20) NOT NULL DEFAULT 'active',
    can_create_subtenants boolean NOT NULL DEFAULT false,
    can_create_campaigns boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    deleted_at timestamptz,

    CONSTRAINT tenants_not_own_parent CHECK (
        parent_tenant_id IS NULL OR parent_tenant_id <> id
    ),
    CONSTRAINT tenants_external_id_not_blank CHECK (btrim(external_id) <> ''),
    CONSTRAINT tenants_name_not_blank CHECK (btrim(name) <> ''),
    CONSTRAINT tenants_email_not_blank CHECK (contact_email IS NULL OR btrim(contact_email) <> ''),
    CONSTRAINT tenants_status_valid CHECK (status IN ('active', 'suspended', 'deleted')),
    CONSTRAINT tenants_deletion_consistent CHECK (
        (status = 'deleted' AND deleted_at IS NOT NULL)
        OR (status <> 'deleted' AND deleted_at IS NULL)
    ),
    CONSTRAINT tenants_parent_external_id_unique UNIQUE (parent_tenant_id, external_id)
);

-- Root identifiers must also be unique; ordinary UNIQUE permits multiple NULL parents.
-- Use external_id = 'nalekci.cz' for the initial root tenant.
-- Deleted identifiers remain reserved for roots and children alike.
CREATE UNIQUE INDEX tenants_root_external_id_unique
    ON tenants (external_id)
    WHERE parent_tenant_id IS NULL;

CREATE INDEX tenants_parent_status_idx ON tenants (parent_tenant_id, status, id);

CREATE TABLE tenant_api_keys (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL REFERENCES tenants (id) ON DELETE RESTRICT,
    purpose varchar(20) NOT NULL,
    label varchar(200),
    secret_hash bytea NOT NULL,
    created_at timestamptz NOT NULL,
    revoked_at timestamptz,

    CONSTRAINT tenant_api_keys_purpose_valid CHECK (purpose IN ('tenant', 'management')),
    -- Proposed verifier: SHA-256 of a cryptographically random secret of at least 32 bytes.
    -- The presented key carries its public key ID and secret; only the digest is persisted.
    CONSTRAINT tenant_api_keys_hash_length CHECK (octet_length(secret_hash) = 32)
);

CREATE INDEX tenant_api_keys_tenant_idx ON tenant_api_keys (tenant_id, created_at, id);

CREATE TABLE tenant_audit_events (
    id uuid PRIMARY KEY,
    actor_api_key_id uuid REFERENCES tenant_api_keys (id) ON DELETE RESTRICT,
    actor_description varchar(200),
    target_tenant_id uuid NOT NULL REFERENCES tenants (id) ON DELETE RESTRICT,
    action varchar(100) NOT NULL,
    details jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL,

    -- Administrative setup has no API key: record its operator in actor_description.
    CONSTRAINT tenant_audit_actor_required CHECK (
        actor_api_key_id IS NOT NULL
        OR (actor_description IS NOT NULL AND btrim(actor_description) <> '')
    ),
    CONSTRAINT tenant_audit_action_not_blank CHECK (btrim(action) <> ''),
    CONSTRAINT tenant_audit_details_object CHECK (jsonb_typeof(details) = 'object')
);

CREATE INDEX tenant_audit_events_target_idx
    ON tenant_audit_events (target_tenant_id, created_at, id);

-- Additional application rules:
-- * Feature 1 permits only root -> direct child, and parent IDs cannot be changed.
-- * Validate email format, input lengths, and nonblank names before saving.
-- * Child creation always assigns both capabilities to false.
-- * Initial nalekci.cz setup enables subtenant creation but keeps campaign creation false.
-- * Campaign creation remains false for all tenants in Feature 1.
-- * Creating a child and its initial key is atomic.
-- * Deletion revokes all child keys and records the change in the same transaction.
-- * Configuration updates and their audit records are atomic.
-- * Authenticate against current tenant/parent status and current capability settings.
-- * Every read/write is scoped to the authenticated tenant or its permitted direct children.
-- * Audit records never contain API secrets or secret hashes.

COMMIT;
