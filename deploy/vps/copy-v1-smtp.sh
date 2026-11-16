#!/bin/sh
# Copies the mail-server settings that the v1 app already uses (EMAIL_* in its .env) into the health-companion stack, ON THE SERVER:
# nothing is printed except the names written, nothing leaves the machine. Writes ~/mevacare-hc/api.email.env (mode 600).
# Run it only if you want both apps to send mail through the same mailbox; otherwise create api.email.env with other settings
# (Email__Host, Email__Port, Email__UseStartTls, Email__Username, Email__Password, Email__FromAddress, Email__FromName).
set -eu
SRC=/root/mevacare_v1_2026/.env
DST=/root/mevacare-hc/api.email.env

[ -f "$SRC" ] || { echo "No $SRC"; exit 1; }

# value of KEY in the v1 .env, without surrounding quotes
get() {
    grep -E "^$1=" "$SRC" | head -n 1 | cut -d= -f2- | sed -e 's/^"//' -e 's/"$//' -e "s/^'//" -e "s/'\$//"
}

HOST=$(get EMAIL_HOST); PORT=$(get EMAIL_PORT); TLS=$(get EMAIL_USE_STARTTLS)
USER_=$(get EMAIL_USERNAME); PASS=$(get EMAIL_PASSWORD); FROM=$(get EMAIL_FROM_ADDRESS)
[ -n "$HOST" ] && [ -n "$FROM" ] || { echo "EMAIL_HOST or EMAIL_FROM_ADDRESS is empty in the v1 .env"; exit 1; }

umask 077
: > "$DST"
for pair in "Email__Host=$HOST" "Email__Port=${PORT:-587}" "Email__UseStartTls=${TLS:-true}" "Email__Username=$USER_" "Email__Password=$PASS" "Email__FromAddress=$FROM" "Email__FromName=MevaCare"; do
    name=${pair%%=*}; value=${pair#*=}
    case "$value" in *"'"*) echo "The value of $name contains a quote; write $DST by hand."; rm -f "$DST"; exit 1;; esac
    printf "%s='%s'\n" "$name" "$value" >> "$DST"
done
chmod 600 "$DST"
echo "Wrote these settings to $DST (values not shown):"
cut -d= -f1 "$DST"
