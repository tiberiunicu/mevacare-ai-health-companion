#!/bin/sh
# Renews the Let's Encrypt certificates of ALL names kept in the v1 certbot volume (mevacare.com and health-companion.mevacare.com).
# Replaces v1's renew-cert.sh, which only looked at the mevacare.com certificate: the newer certificate would have expired unnoticed.
# Does nothing (and causes no downtime) until some certificate expires within 30 days. The `web` container is always started again,
# even if renewal fails, so a failed renewal can never leave the sites down.
set -u
cd /root/mevacare_v1_2026

MOUNT=$(docker volume inspect mevacare_v1_2026_certbot_conf --format '{{.Mountpoint}}')
DUE=0
for CERT in "$MOUNT"/live/*/fullchain.pem; do
    if ! openssl x509 -checkend $((30*86400)) -noout -in "$CERT" >/dev/null 2>&1; then
        DUE=1
    fi
done
[ "$DUE" = 0 ] && exit 0

docker compose stop web
docker compose run --rm --service-ports certbot renew --standalone --quiet
STATUS=$?
docker compose up -d web
exit $STATUS
