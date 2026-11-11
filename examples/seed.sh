#!/usr/bin/env bash
# Creates demo links and simulates varied visitors through the real HTTP API.
#
#   CURTO_API_KEY=<key> ./examples/seed.sh [base-url] [clicks] [delay-seconds]
#
#   base-url        default http://127.0.0.1:5080 (or $CURTO_URL)
#   clicks          number of simulated visits, default 600
#   delay-seconds   pause between visits, default 0 (try 0.2 to watch the Live view tick)
#
# Every visit is a real GET /{slug} that is not followed, with a browser User-Agent and an optional Referer, so the
# traffic goes through the same pipeline as production clicks. The random generator is seeded: the same arguments
# produce the same traffic mix. Visits go through the redirect rate limit (120 per minute by default); the script
# waits and retries when it is told to slow down. For a fast run start the server with
# CURTO_RATE_LIMIT_REDIRECT_PER_MINUTE=100000.
set -euo pipefail

BASE_URL="${1:-${CURTO_URL:-http://127.0.0.1:5080}}"
BASE_URL="${BASE_URL%/}"
CLICKS="${2:-600}"
DELAY="${3:-0}"
KEY="${CURTO_API_KEY:?Set CURTO_API_KEY to the key the server runs with}"
RANDOM=2026

# slug|target|title|tags|share of the traffic
LINKS='launch|https://blog.example.com/curto-launch|Launch announcement|blog,launch|30
guide|https://docs.example.com/getting-started|Getting started guide|docs|14
newsletter|https://news.example.org/issues/42|Newsletter issue 42|email,newsletter|12
podcast|https://audio.example.net/episodes/17|Podcast episode 17|podcast,audio|9
pricing|https://www.example.com/pricing|Pricing page|product|8
github|https://code.example.com/curto|Source code|code,oss|7
changelog|https://docs.example.com/changelog|Changelog|docs,release|6
webinar|https://events.example.org/webinar-q4|Q4 webinar|event|5
q4-report|https://files.example.net/reports/2026-q4.pdf|Q4 report (PDF)|report|4
survey|https://forms.example.com/community-survey|Community survey|research|3
invite|https://app.example.org/invite/early-access|Early access invite|growth|2'

# Browser templates and their share of the visitors. {v} and {p} make every simulated visitor's User-Agent unique.
UA_TEMPLATES=(
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{v}.0.{p}.0 Safari/537.36'
  'Mozilla/5.0 (iPhone; CPU iPhone OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E{p} Safari/604.1'
  'Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{v}.0.{p}.0 Mobile Safari/537.36'
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.{p} Safari/605.1.15'
  'Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{v}.0.{p}.0 Safari/537.36'
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/{v}.0.{p}.0 Safari/537.36 Edg/{v}.0.{p}.0'
  'Mozilla/5.0 (X11; Linux x86_64; rv:{v}.{p}) Gecko/20100101 Firefox/{v}.{p}'
  'Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:{v}.{p}) Gecko/20100101 Firefox/{v}.{p}'
  'Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) SamsungBrowser/24.{p} Chrome/{v}.0.0.0 Mobile Safari/537.36'
  'Mozilla/5.0 (iPad; CPU OS 17_4 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.4 Mobile/15E{p} Safari/604.1'
  'Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)'
)
UA_WEIGHTS=(26 17 13 8 7 6 4 3 3 3 2)

# Referer values (empty means direct traffic) and their share.
REFERRERS=('' 'https://news.ycombinator.com/item?id=41234567' 'https://t.co/aB3dE5' 'https://www.linkedin.com/feed/'
  'https://www.reddit.com/r/selfhosted/' 'https://www.google.com/' 'https://github.com/q6r/curto' 'https://lobste.rs/'
  'https://bsky.app/' 'https://duckduckgo.com/')
REFERRER_WEIGHTS=(30 14 12 10 9 8 6 4 4 3)

iso_at() { # Unix time as an ISO 8601 UTC instant (GNU date first, then BSD date)
  date -u -d "@$1" +%Y-%m-%dT%H:%M:%SZ 2>/dev/null || date -u -r "$1" +%Y-%m-%dT%H:%M:%SZ
}

PICK=0
pick() { # weighted random index into the weights given as arguments; sets PICK (no subshell, so RANDOM advances)
  local total=0 w i=0
  for w in "$@"; do total=$((total + w)); done
  local r=$(( (RANDOM * 32768 + RANDOM) % total ))
  for w in "$@"; do
    if [ "$r" -lt "$w" ]; then PICK=$i; return; fi
    r=$((r - w)); i=$((i + 1))
  done
}

create_link() { # slug target title tags-csv [extra JSON members]
  local tags="[]" extra="${5:-}"
  if [ -n "$4" ]; then tags="[\"${4//,/\",\"}\"]"; fi
  local code
  code=$(curl -sS -o /dev/null -w '%{http_code}' -X POST "$BASE_URL/api/links" \
    -H "X-API-Key: $KEY" -H 'Content-Type: application/json' \
    -d "{\"slug\":\"$1\",\"url\":\"$2\",\"title\":\"$3\",\"tags\":$tags${extra:+,$extra}}")
  case "$code" in
    201) echo "  created /$1" ;;
    409) echo "  /$1 already exists, keeping it" ;;
    *) echo "  creating /$1 failed with HTTP $code" >&2; exit 1 ;;
  esac
}

hit() { # slug user-agent referer: prints the HTTP status of one visit, waiting and retrying while rate limited
  local args=(-sS -o /dev/null -D - -H "User-Agent: $2") out code wait
  if [ -n "$3" ]; then args+=(-H "Referer: $3"); fi
  while :; do
    out=$(curl "${args[@]}" "$BASE_URL/$1" | tr -d '\r')
    code=$(printf '%s\n' "$out" | head -n 1 | awk '{print $2}')
    if [ "$code" != "429" ]; then printf '%s' "$code"; return; fi
    wait=$(printf '%s\n' "$out" | awk -F': ' 'tolower($1) == "retry-after" { print $2 }')
    echo "  rate limited, waiting ${wait:-5}s (raise CURTO_RATE_LIMIT_REDIRECT_PER_MINUTE for faster seeding)" >&2
    sleep "${wait:-5}"
  done
}

curl -fsS "$BASE_URL/healthz" > /dev/null || { echo "No curto server answers at $BASE_URL" >&2; exit 1; }

echo "Creating links at $BASE_URL"
slugs=() weights=()
while IFS='|' read -r slug target title tags share; do
  create_link "$slug" "$target" "$title" "$tags"
  slugs+=("$slug"); weights+=("$share")
done <<< "$LINKS"
create_link flash-sale https://shop.example.com/flash-sale "Flash sale (25 clicks)" campaign '"maxClicks":25'
beta_expiry=$(( $(date +%s) + 8 ))
create_link beta-signup https://beta.example.org/signup "Beta signup (expires in seconds)" campaign "\"expiresAt\":\"$(iso_at "$beta_expiry")\""

# A pool of simulated visitors: each one keeps the same User-Agent, so some of them come back.
visitors=$(( CLICKS * 10 / 22 )); [ "$visitors" -ge 1 ] || visitors=1
v_tpl=() v_ver=() v_patch=()
for ((i = 0; i < visitors; i++)); do
  pick "${UA_WEIGHTS[@]}"
  v_tpl+=("$PICK"); v_ver+=($((118 + RANDOM % 8))); v_patch+=($((RANDOM % 9000 + 100)))
done

ok=0 gone=0 other=0
visit() { # slug: one visit by a random visitor from the pool; updates the ok, gone and other counters
  local v=$(( (RANDOM * 32768 + RANDOM) % visitors )) ua referrer
  ua="${UA_TEMPLATES[${v_tpl[$v]}]}"; ua="${ua//\{v\}/${v_ver[$v]}}"; ua="${ua//\{p\}/${v_patch[$v]}}"
  pick "${REFERRER_WEIGHTS[@]}"; referrer="${REFERRERS[$PICK]}"
  case "$(hit "$1" "$ua" "$referrer")" in 302) ok=$((ok + 1)) ;; 410) gone=$((gone + 1)) ;; *) other=$((other + 1)) ;; esac
}

for ((n = 0; n < 3; n++)); do visit beta-signup; done   # it is still valid for a few seconds

echo "Simulating $CLICKS visits by $visitors visitors"
for ((n = 1; n <= CLICKS; n++)); do
  pick "${weights[@]}"; visit "${slugs[$PICK]}"
  if [ $((n % 100)) -eq 0 ]; then echo "  $n / $CLICKS"; fi
  if [ "$DELAY" != "0" ]; then sleep "$DELAY"; fi
done

echo "Spending the click budget of /flash-sale (25 clicks, then HTTP 410)"
for ((n = 1; n <= 30; n++)); do visit flash-sale; done

echo "Checking that /beta-signup has expired"
remaining=$(( beta_expiry + 1 - $(date +%s) ))
if [ "$remaining" -gt 0 ]; then sleep "$remaining"; fi
visit beta-signup

echo "Done: $ok redirects recorded, $gone refused with 410, $other other."
echo "Open $BASE_URL/ and enter the API key."
