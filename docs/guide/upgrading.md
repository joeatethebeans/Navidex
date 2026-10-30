# Upgrading

## To a new version

The compose file runs the release named by `MELODEX_VERSION` (`latest` by default). To upgrade:

```bash
docker compose pull
docker compose up -d
```

The database is migrated when the core starts. Back up the `config` volume first if you want a way back: an older
version can't open a database a newer one has migrated. What changed in each release is in the
[changelog](https://github.com/joeatethebeans/Melodex/blob/main/CHANGELOG.md).

## From Navidex

Melodex was called Navidex. An install from then upgrades in place, with nothing to do by hand:

- The database file, `navidex.db`, is renamed `melodex.db` on first start.
- Plugins carry on: their IDs, once `navidex.*`, are rewritten.
- API keys starting `ndx_` keep working. New keys start `mdx_`.
- `Navidex__…` environment variables are still read.
- Everyone signs in once more, as the session cookie has a new name.
- Webhook deliveries now carry `Melodex-Signature`, `Melodex-Event` and `Melodex-Delivery` headers; update any
  receiver that checks the old `Navidex-` names.

To move your compose setup over:

1. Stop the old stack: `docker compose down` (this keeps its volumes).
2. Replace `docker-compose.yml` with the new one, **in the same folder**. Compose names volumes after the folder, so
   keeping it keeps your `config` volume and its database. If you must use another folder, set
   `COMPOSE_PROJECT_NAME` to the old folder's name.
3. Start it: `docker compose up -d`.

The core's log says *Moved the database from Navidex* on that first start.
