#!/bin/bash
# Runs a THOBOTTO image as a bot, on the Postgres server on localhost, until its health answers "ok <version>", then
# stops it. Usage: run-image.sh <image> <version>, with the bot's token in CI_BOT_TOKEN. Lavalink is pointed at
# nothing: the bot starts before Lavalink on a stack too, and health doesn't need it.
set -euo pipefail
image=$1
version=$2
docker rm -f thobotto >/dev/null 2>&1 || true
docker run -d --name thobotto --network host \
  -e Discord__Token="$CI_BOT_TOKEN" \
  -e "ConnectionStrings__Postgres=Host=localhost;Database=thobotto;Username=postgres;Password=ci" \
  -e Lavalink__BaseAddress=http://localhost:2333 -e Lavalink__Passphrase=ci \
  "$image" >/dev/null
answer=""
for i in $(seq 1 60); do
  answer=$(curl -s --max-time 5 http://localhost:8080/health || true)
  if [ "$answer" = "ok $version" ]; then
    echo "healthy: $answer after $i checks"
    docker rm -f thobotto >/dev/null
    exit 0
  fi
  sleep 5
done
echo "never healthy: ${answer:-no answer}"
docker logs thobotto 2>&1 | tail -n 80
docker rm -f thobotto >/dev/null
exit 1
