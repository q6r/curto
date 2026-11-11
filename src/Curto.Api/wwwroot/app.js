/* curto dashboard.
 *
 * Talks to the documented HTTP API only. This is a classic script without any inline code anywhere, so it runs under
 * the strict Content Security Policy the server sends (script-src 'self'; style-src 'self'). Text reaches the DOM
 * through the UI kit's el() helper (textContent), never as HTML. The API key lives in sessionStorage and travels in the
 * X-API-Key header; IP addresses never reach this page because the API does not expose them.
 *
 * The page is built by Aurora.shell(): a rail (Overview, Links, Live), a top bar (breadcrumbs, search, live state and
 * "New link"), a sub bar that every view fills with its own controls (range, status filter, counts) and a status bar.
 */
(function () {
  'use strict';

  const { el, fmt } = Aurora;
  const AC = AuroraCharts;

  /* ───────────────────────── constants and small helpers ───────────────────────── */

  const KEY_STORAGE = 'curto.apiKey';
  const PAGE_SIZE = 25;
  const RANGES = [
    { id: '24h', short: '24 h', label: 'Last 24 hours', period: 'previous 24 hours' },
    { id: '7d', short: '7 d', label: 'Last 7 days', period: 'previous 7 days' },
    { id: '30d', short: '30 d', label: 'Last 30 days', period: 'previous 30 days' },
    { id: '90d', short: '90 d', label: 'Last 90 days', period: 'previous 90 days' },
  ];
  const SORTS = [
    { id: 'newest', label: 'Newest first', sort: 'createdAt', dir: 'desc' },
    { id: 'oldest', label: 'Oldest first', sort: 'createdAt', dir: 'asc' },
    { id: 'most', label: 'Most clicks', sort: 'clicks', dir: 'desc' },
    { id: 'least', label: 'Fewest clicks', sort: 'clicks', dir: 'asc' },
  ];
  /** `status` is what the API takes in ?status=; the empty string means no filter. */
  const STATUSES = [
    { id: 'all', status: '', label: 'All' },
    { id: 'active', status: 'active', label: 'Active' },
    { id: 'expired', status: 'expired', label: 'Expired' },
    { id: 'exhausted', status: 'exhausted', label: 'Limit reached' },
  ];
  /** The rail destinations, in order; each one is a hash route. */
  const ROUTES = [
    { id: 'overview', label: 'Overview', icon: 'bar-chart' },
    { id: 'links', label: 'Links', icon: 'link' },
    { id: 'live', label: 'Live', icon: 'activity' },
  ];

  const $ = (selector, scope) => (scope || document).querySelector(selector);
  const clear = (node) => { while (node.firstChild) node.removeChild(node.firstChild); return node; };
  const icon = (name, size) => Aurora.icon(name, { size: size || 16 });
  const pad2 = (n) => String(n).padStart(2, '0');
  const rangeById = (id) => RANGES.find((r) => r.id === id) || RANGES[1];
  const routeById = (id) => ROUTES.find((r) => r.id === id) || ROUTES[0];
  const statusByValue = (value) => STATUSES.find((s) => s.status === value) || STATUSES[0];
  const hostOf = (url) => { try { return new URL(url).host; } catch (e) { return url; } };
  const dayOf = (iso) => String(iso).slice(0, 10);
  const dayShort = new Intl.DateTimeFormat('en-US', { month: 'short', day: 'numeric', timeZone: 'UTC' });
  const dayLong = new Intl.DateTimeFormat('en-US', { weekday: 'short', year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' });
  const asUtcDay = (day) => new Date(`${day}T00:00:00Z`);
  const clock = (d) => `${pad2(d.getHours())}:${pad2(d.getMinutes())}:${pad2(d.getSeconds())}`;
  const share = (value, total) => (total > 0 ? fmt.pct(value / total, 1) : '0%');
  const plural = (n, one, many) => `${fmt.int(n)} ${n === 1 ? one : many}`;
  /** "2026-11-10 14:03 (3h ago)"; the UI kit's relative time turns into a plain date after a month, which would repeat itself. */
  const when = (iso) => {
    const relative = fmt.relative(iso);
    return /^\d{4}-/.test(relative) ? fmt.date(iso, { time: true }) : `${fmt.date(iso, { time: true })} (${relative})`;
  };
  const toLocalInput = (iso) => {
    const d = new Date(iso);
    return `${d.getFullYear()}-${pad2(d.getMonth() + 1)}-${pad2(d.getDate())}T${pad2(d.getHours())}:${pad2(d.getMinutes())}`;
  };
  const fromLocalInput = (value) => new Date(value).toISOString().replace(/\.\d{3}Z$/, 'Z');
  const parseTags = (text) => text.split(',').map((t) => t.trim().toLowerCase()).filter((t, i, all) => t && all.indexOf(t) === i);

  /** Builds a button; with no label it becomes a square icon button (aria is then required). */
  function button(label, o) {
    o = o || {};
    const classes = ['a-btn'];
    if (o.kind) classes.push(`a-btn--${o.kind}`);
    if (o.small) classes.push('a-btn--sm');
    if (!label) classes.push('a-btn--icon');
    if (o.extra) classes.push(o.extra);
    return el('button', { class: classes.join(' '), type: 'button', title: o.title, 'aria-label': o.aria, disabled: o.disabled, onclick: o.onClick },
      o.icon ? icon(o.icon, o.small ? 15 : 16) : null, label ? el('span', label) : null);
  }

  /** A small pill for the sub bar: an icon and a text that can change (a count), or a button when onClick is given. */
  function pill(iconName, text, o) {
    o = o || {};
    const label = text ? el('span', text) : null;
    const node = el(o.onClick ? 'button' : 'span', { class: o.count ? 'a-pill c-pill--count' : 'a-pill', type: o.onClick ? 'button' : null, title: o.title, 'aria-label': o.aria, onclick: o.onClick }, icon(iconName, 14), label);
    return { el: node, set(next) { if (label) label.textContent = next; } };
  }

  /** Options of a range switch (the view switch of the sub bar, or the one in the link drawer). */
  const rangeOptions = (active, onChange) => ({
    items: RANGES.map((r) => ({ id: r.id, label: r.short, title: r.label })), active, onChange, ariaLabel: 'Date range',
  });

  /** Gives every item of a switch its long label as accessible name: the bar shows "7 d", a screen reader says "Last 7 days". */
  function nameItems(switcher, items) {
    items.forEach((item) => {
      const node = switcher.el.querySelector(`[data-id="${item.id}"]`);
      if (node && item.title) node.setAttribute('aria-label', item.title);
    });
    return switcher;
  }

  /** Folds everything beyond the first (max - 1) entries, and any entry the API itself calls "Other", into one last part. */
  function foldTail(items, max) {
    const sum = (list) => list.reduce((total, item) => total + item.clicks, 0);
    const other = sum(items.filter((item) => item.name === 'Other'));
    const named = items.filter((item) => item.name !== 'Other');
    const head = named.slice(0, other > 0 || named.length > max ? max - 1 : max);
    const rest = other + sum(named.slice(head.length));
    return rest > 0 ? head.concat([{ name: 'Other', clicks: rest }]) : head;
  }

  /* ───────────────────────── API key and HTTP client ───────────────────────── */

  const memory = { key: '' };
  const store = {
    get() { try { return sessionStorage.getItem(KEY_STORAGE) || memory.key; } catch (e) { return memory.key; } },
    set(value) { memory.key = value; try { sessionStorage.setItem(KEY_STORAGE, value); } catch (e) { /* storage blocked: keep it in memory only */ } },
    clear() { memory.key = ''; try { sessionStorage.removeItem(KEY_STORAGE); } catch (e) { /* storage blocked */ } },
  };

  class ApiError extends Error {
    constructor(status, problem, message, retryAfter) {
      super(message);
      this.name = 'ApiError';
      this.status = status;
      this.problem = problem;
      this.retryAfter = retryAfter || 0;
    }
    get fieldErrors() { return (this.problem && this.problem.errors) || {}; }
  }

  const auth = (() => {
    let key = store.get();
    let prompting = null;
    let dismissed = false;
    const listeners = new Set();
    return {
      get key() { return key; },
      /** True after the user closed the key dialog without a key: requests then fail quietly instead of asking again. */
      get dismissed() { return dismissed; },
      has() { return !!key; },
      set(next) {
        key = next || '';
        if (key) store.set(key); else store.clear();
        listeners.forEach((fn) => fn(!!key));
      },
      onChange(fn) { listeners.add(fn); },
      /** Resolves true once a key the server accepts is stored, false if the dialog was dismissed. One dialog at a time. */
      ensure(reason) {
        if (!prompting) prompting = promptForKey(reason).then((ok) => { prompting = null; dismissed = !ok; return ok; });
        return prompting;
      },
    };
  })();

  function promptForKey(reason) {
    return new Promise((resolve) => {
      let finished = false;
      const input = el('input', {
        class: 'a-input', id: 'api-key', name: 'api-key', type: 'password', autocomplete: 'off', spellcheck: 'false', autofocus: true,
        placeholder: 'Paste the API key', 'aria-describedby': 'api-key-error',
      });
      const error = el('div', { class: 'a-field__error', id: 'api-key-error', role: 'alert' });
      const content = el('form', { class: 'c-form', novalidate: true, onsubmit: (event) => { event.preventDefault(); submit(); } },
        el('p', { class: 'a-muted' }, reason || 'Every call to the API needs the key the server was started with (CURTO_API_KEY).'),
        el('div', { class: 'a-field' }, el('label', { for: 'api-key' }, 'API key'), input, error),
        el('p', { class: 'a-faint c-small' }, 'The key stays in this tab\'s session storage and is sent as the X-API-Key header. Nothing else is stored.'));
      const dialog = Aurora.dialog({
        title: 'API key required', size: 'sm', content,
        actions: [{ label: 'Connect', kind: 'primary', closes: false, onClick: () => { submit(); return false; } }],
        onClose: () => { if (!finished) { finished = true; resolve(false); } },
      });
      setTimeout(() => input.focus(), 0);

      async function submit() {
        const candidate = input.value.trim();
        input.removeAttribute('aria-invalid');
        if (!candidate) { error.textContent = 'Enter the key first.'; input.setAttribute('aria-invalid', 'true'); return; }
        error.textContent = 'Checking…';
        try {
          const res = await fetch('/api/links?limit=1', { headers: { Accept: 'application/json', 'X-API-Key': candidate }, cache: 'no-store' });
          if (res.status === 401) { error.textContent = 'The server rejected that key.'; input.setAttribute('aria-invalid', 'true'); input.select(); return; }
          if (!res.ok && res.status !== 429) { error.textContent = `The server answered HTTP ${res.status}.`; return; }
          auth.set(candidate);
          finished = true;
          dialog.close();
          resolve(true);
        } catch (e) {
          error.textContent = 'Cannot reach the server.';
        }
      }
    });
  }

  async function request(method, path, o) {
    o = o || {};
    const params = new URLSearchParams();
    Object.keys(o.query || {}).forEach((name) => {
      const value = o.query[name];
      if (value !== undefined && value !== null && value !== '') params.set(name, String(value));
    });
    const url = params.toString() ? `${path}?${params}` : path;

    for (let attempt = 0; ; attempt++) {
      // Without a key every call would be refused: ask first (once), or fail quietly after the dialog was dismissed.
      if (!auth.key && (auth.dismissed || !(await auth.ensure()))) throw new ApiError(401, null, 'An API key is required.');
      const headers = { Accept: 'application/json', 'X-API-Key': auth.key };
      let body;
      if (o.body !== undefined) { headers['Content-Type'] = o.contentType || 'application/json'; body = JSON.stringify(o.body); }

      let res;
      try {
        res = await fetch(url, { method, headers, body, cache: 'no-store', signal: o.signal });
      } catch (e) {
        if (e && e.name === 'AbortError') throw e;
        throw new ApiError(0, null, 'Cannot reach the server. Is it running?');
      }
      if (res.status === 401 && attempt === 0 && !auth.dismissed) {
        if (await auth.ensure()) continue;
        throw new ApiError(401, null, 'An API key is required.');
      }
      if (res.status === 204) return null;

      const text = await res.text();
      let data = null;
      try { data = text ? JSON.parse(text) : null; } catch (e) { data = null; }
      if (!res.ok) {
        throw new ApiError(res.status, data, (data && (data.detail || data.title)) || `HTTP ${res.status}`, Number(res.headers.get('Retry-After')) || 0);
      }
      return data;
    }
  }

  const api = {
    get: (path, query) => request('GET', path, { query }),
    post: (path, body) => request('POST', path, { body }),
    patch: (path, body) => request('PATCH', path, { body, contentType: 'application/merge-patch+json' }),
    del: (path) => request('DELETE', path),
  };

  /* ───────────────────────── live stream (fetch + stream reader, with back-off) ───────────────────────── */

  /* EventSource cannot send the X-API-Key header, so the stream is read with fetch(). */
  const live = (() => {
    const clickListeners = new Set();
    const stateListeners = new Set();
    let state = 'offline';
    let running = false;
    let controller = null;
    let retryTimer = null;
    let watchdog = null;
    let attempt = 0;

    const setState = (next, text) => { state = next; stateListeners.forEach((fn) => fn(next, text)); };

    function armWatchdog() {
      clearTimeout(watchdog);
      // The server sends a heartbeat every 15 seconds by default; silence for three times that means a dead connection.
      watchdog = setTimeout(() => { if (controller) controller.abort(); }, 45000);
    }

    function handleFrame(raw) {
      let name = 'message';
      const data = [];
      raw.split(/\r?\n/).forEach((line) => {
        if (!line || line[0] === ':') return;
        const colon = line.indexOf(':');
        const field = colon < 0 ? line : line.slice(0, colon);
        let value = colon < 0 ? '' : line.slice(colon + 1);
        if (value[0] === ' ') value = value.slice(1);
        if (field === 'event') name = value; else if (field === 'data') data.push(value);
      });
      if (name !== 'click' || !data.length) return;
      let click;
      try { click = JSON.parse(data.join('\n')); } catch (e) { return; }
      clickListeners.forEach((fn) => fn(click));
    }

    function scheduleReconnect(retryAfterSeconds) {
      const base = Math.min(30000, 1000 * Math.pow(2, attempt));
      attempt += 1;
      const delay = Math.max(retryAfterSeconds * 1000, base * (0.75 + Math.random() * 0.5));
      setState('offline', `Reconnecting in ${Math.ceil(delay / 1000)}s`);
      retryTimer = setTimeout(connect, delay);
    }

    async function connect() {
      if (!running) return;
      controller = new AbortController();
      const mine = controller;
      setState('connecting');
      let retryAfter = 0;
      try {
        const res = await fetch('/api/events', { headers: { Accept: 'text/event-stream', 'X-API-Key': auth.key }, cache: 'no-store', signal: mine.signal });
        if (res.status === 401) {
          running = false;
          setState('offline', 'API key rejected');
          auth.ensure('The live stream was refused. Enter a valid key to continue.').then((ok) => { if (ok) live.start(); });
          return;
        }
        if (res.status === 503) retryAfter = Number(res.headers.get('Retry-After')) || 5;
        if (!res.ok || !res.body) throw new Error(`HTTP ${res.status}`);

        setState('live');
        attempt = 0;
        armWatchdog();
        const reader = res.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';
        for (;;) {
          const { value, done } = await reader.read();
          if (done) break;
          armWatchdog();
          buffer += decoder.decode(value, { stream: true });
          let cut = buffer.search(/\r?\n\r?\n/);
          while (cut >= 0) {
            handleFrame(buffer.slice(0, cut));
            buffer = buffer.slice(cut).replace(/^\r?\n\r?\n/, '');
            cut = buffer.search(/\r?\n\r?\n/);
          }
        }
      } catch (e) {
        if (!running) return;
      } finally {
        clearTimeout(watchdog);
      }
      if (running && controller === mine) scheduleReconnect(retryAfter);
    }

    window.addEventListener('online', () => {
      if (running && state !== 'live') { clearTimeout(retryTimer); attempt = 0; connect(); }
    });

    return {
      start() { if (running || !auth.has()) return; running = true; attempt = 0; connect(); },
      stop() {
        running = false;
        clearTimeout(retryTimer);
        clearTimeout(watchdog);
        if (controller) controller.abort();
        setState('offline');
      },
      onClick(fn) { clickListeners.add(fn); return () => clickListeners.delete(fn); },
      onState(fn) { stateListeners.add(fn); fn(state); return () => stateListeners.delete(fn); },
      get state() { return state; },
    };
  })();

  /** What the Live view shows: counters for this browser session, kept even while another view is open. */
  const session = (() => {
    const WINDOW_MS = 5 * 60 * 1000;
    let s;
    const reset = () => { s = { since: Date.now(), total: 0, stamps: [], slugs: new Map(), referrers: new Map(), mobile: 0, feed: [] }; };
    reset();
    live.onClick((click) => {
      const now = Date.now();
      s.total += 1;
      s.stamps.push(now);
      while (s.stamps.length && now - s.stamps[0] > WINDOW_MS) s.stamps.shift();
      s.slugs.set(click.slug, (s.slugs.get(click.slug) || 0) + 1);
      s.referrers.set(click.referrer, (s.referrers.get(click.referrer) || 0) + 1);
      if (click.device === 'mobile') s.mobile += 1;
      s.feed.unshift({ click, at: new Date(now) });
      if (s.feed.length > 40) s.feed.pop();
    });
    return {
      reset,
      get total() { return s.total; },
      get since() { return s.since; },
      get feed() { return s.feed; },
      distinctLinks() { return s.slugs.size; },
      distinctReferrers() { return s.referrers.size; },
      mobileShare() { return s.total ? s.mobile / s.total : 0; },
      perMinute(now) { return s.stamps.filter((t) => now - t <= 60000).length; },
      topLinks(limit) {
        return Array.from(s.slugs, ([slug, clicks]) => ({ slug, clicks })).sort((a, b) => b.clicks - a.clicks || a.slug.localeCompare(b.slug)).slice(0, limit);
      },
      /** Clicks per 10 seconds over the last five minutes, oldest first. */
      series(now) {
        const step = 10000;
        const end = Math.floor(now / step) * step;
        const points = [];
        for (let i = 29; i >= 0; i--) {
          const from = end - i * step;
          points.push({ x: from, y: s.stamps.filter((t) => t >= from && t < from + step).length });
        }
        return points;
      },
    };
  })();

  /* ───────────────────────── shared building blocks ───────────────────────── */

  const STATUS_BADGE = {
    active: ['Active', 'good'],
    expired: ['Expired', 'warn'],
    exhausted: ['Limit reached', 'serious'],
  };

  function statusBadge(status) {
    const hit = STATUS_BADGE[status] || [String(status), 'neutral'];
    return Aurora.badge(hit[0], { kind: hit[1] });
  }

  /** A pill-shaped, keyboard-reachable tag that applies a filter when it is pressed. */
  function tagButton(tag, onPick) {
    return el('button', { class: 'a-tag c-tag', type: 'button', title: `Show links tagged ${tag}`, onclick: (event) => { event.stopPropagation(); onPick(tag); } }, tag);
  }

  function errorState(parent, error, retry) {
    const unauthorized = error && error.status === 401;
    return Aurora.empty(parent, {
      icon: unauthorized ? 'lock' : 'alert',
      title: unauthorized ? 'API key required' : 'Could not load this view',
      text: unauthorized ? 'Enter the key the server was started with to see your data.' : (error && error.message) || 'Something went wrong.',
      action: {
        label: unauthorized ? 'Enter API key' : 'Try again',
        onClick: () => { (unauthorized ? auth.ensure() : Promise.resolve(true)).then((ok) => { if (ok) retry(); }); },
      },
    });
  }

  /** Part-to-whole bars for at most six parts; the legend and the Table view carry the numbers. Returns the chart card. */
  function audienceCard(parent) {
    const sections = [['devices', 'Devices'], ['browsers', 'Browsers'], ['os', 'Operating systems']];
    const slots = {};
    let latest = {};
    return AC.card(parent, {
      title: 'Audience',
      subtitle: 'Share of clicks by device, browser and operating system',
      render: (body) => {
        const host = el('div', { class: 'c-audience' });
        body.appendChild(host);
        sections.forEach(([key, label]) => {
          slots[key] = el('div', { class: 'c-audience__item' }, el('div', { class: 'a-eyebrow' }, label));
          host.appendChild(slots[key]);
        });
        return {
          // A new segbar per update instead of segbar.update(): the UI kit's tall-bar label pass runs one animation frame
          // later and reads the bar's current children with the part list of the render that scheduled it.
          update(data) {
            latest = data;
            sections.forEach(([key]) => {
              const items = foldTail(data[key] || [], 6);
              slots[key].querySelectorAll('.a-segbar, .c-audience__empty').forEach((node) => node.remove());
              if (!items.length) {
                slots[key].appendChild(el('div', { class: 'a-faint c-small c-audience__empty' }, 'No clicks in this range yet'));
                return;
              }
              Aurora.segbar(slots[key], { parts: items.map((item) => ({ name: item.name, value: item.clicks })), tall: true, valueFormat: fmt.int });
            });
          },
          table() {
            const rows = [];
            sections.forEach(([key, label]) => {
              const items = foldTail(latest[key] || [], 6);
              const total = items.reduce((sum, item) => sum + item.clicks, 0);
              items.forEach((item) => rows.push({ group: label, name: item.name, clicks: item.clicks, share: share(item.clicks, total) }));
            });
            return {
              caption: 'Clicks by device, browser and operating system',
              columns: [{ key: 'group', label: 'Dimension' }, { key: 'name', label: 'Value' }, { key: 'clicks', label: 'Clicks', align: 'right', format: (v) => fmt.int(v) }, { key: 'share', label: 'Share', align: 'right' }],
              rows,
            };
          },
        };
      },
    });
  }

  function rankedBars(body, o) {
    return AC.bars(body, Object.assign({ orientation: 'horizontal', valueFormat: fmt.int, valueLabel: 'Clicks', emptyText: 'No clicks in this range yet' }, o));
  }

  function barData(items, total) {
    return items.map((item) => ({ id: item.name, label: item.name, value: item.clicks, sub: `${share(item.clicks, total)} of clicks` }));
  }

  /**
   * y-axis options for a series of whole numbers. Below a peak of four the UI kit's tick step is fractional and its labels
   * print as repeated integers (0 0 1 1 1), so a small series gets an axis fixed at 0 to 4.
   */
  function countAxis(values) {
    const peak = values.length ? Math.max.apply(null, values) : 0;
    return { min: 0, max: peak < 4 ? 4 : undefined, format: fmt.compact };
  }

  function lineOptions(stats, range, extra) {
    const hourly = stats.range.bucket === 'hour';
    const points = (key) => stats.series.map((p) => ({ x: hourly ? p.t : dayOf(p.t), y: p[key] }));
    const empty = stats.clicks.inRange === 0;
    return Object.assign({
      series: empty ? [] : [
        { id: 'clicks', name: 'Clicks', color: 1, values: points('clicks') },
        { id: 'visitors', name: 'Visitors', color: 2, values: points('visitors') },
      ],
      x: hourly
        ? { type: 'time', label: 'Hour' }
        : {
          type: 'band',
          label: 'Day (UTC)',
          format: (day) => dayShort.format(asUtcDay(day)),
          tooltipFormat: (day) => `${dayLong.format(asUtcDay(day))} (UTC)`,
        },
      y: countAxis(points('clicks').concat(points('visitors')).map((p) => p.y)),
      legend: 'always',
      valueFormat: fmt.int,
      title: 'Clicks and visitors',
      ariaLabel: `Clicks and visitors, ${range.label.toLowerCase()}`,
      emptyText: 'No clicks in this range yet',
    }, extra || {});
  }

  /* ───────────────────────── link form (create and edit) ───────────────────────── */

  function linkForm(o) {
    const link = o.link || null;
    const initial = o.values || {};
    const fields = {};
    const general = el('div', { class: 'c-error', role: 'alert', hidden: true });

    const spec = [
      { name: 'url', label: 'Destination URL', type: 'url', placeholder: 'https://example.com/a/long/path', max: 2048, hint: 'Absolute http or https URL.', value: link ? link.url : initial.url || '', autofocus: !link },
    ];
    if (!link) {
      spec.push({ name: 'slug', label: 'Custom slug', type: 'text', placeholder: 'launch-2026', max: 32, hint: '3-32 letters, digits, "_" or "-". Leave empty for a random one.' });
    }
    spec.push(
      { name: 'title', label: 'Title', type: 'text', placeholder: 'Launch announcement', max: 200, hint: 'Optional label shown in the dashboard.', value: link ? link.title || '' : '' },
      { name: 'tags', label: 'Tags', type: 'text', placeholder: 'blog, launch', hint: 'Comma separated. Up to 10; letters, digits, - _ . and :', value: link ? link.tags.join(', ') : '' },
      { name: 'expiresAt', label: 'Expires', type: 'datetime-local', hint: 'Optional, in your local time. Afterwards the link answers 410.', value: link && link.expiresAt ? toLocalInput(link.expiresAt) : '' },
      { name: 'maxClicks', label: 'Click limit', type: 'number', min: 1, placeholder: 'No limit', hint: 'Optional. The link stops redirecting after this many clicks.', value: link && link.maxClicks ? String(link.maxClicks) : '' },
    );

    const idPrefix = Aurora.uid('lf');
    const grid = el('div', { class: 'c-form__grid' });
    spec.forEach((f) => {
      const id = `${idPrefix}-${f.name}`;
      const input = el('input', {
        class: 'a-input', id, name: f.name, type: f.type, placeholder: f.placeholder, maxlength: f.max, min: f.min, autocomplete: 'off', spellcheck: 'false',
        autofocus: f.autofocus, 'aria-describedby': `${id}-hint ${id}-error`,
      });
      if (f.value) input.value = f.value;
      const error = el('div', { class: 'a-field__error', id: `${id}-error`, 'aria-live': 'polite' });
      const wrapper = el('div', { class: `a-field c-form__${f.name}` }, el('label', { for: id }, f.label), input, el('div', { class: 'a-field__hint', id: `${id}-hint` }, f.hint), error);
      fields[f.name] = { input, error };
      grid.appendChild(wrapper);
    });

    const form = el('form', { class: 'c-form', novalidate: true, onsubmit: (event) => { event.preventDefault(); if (o.onSubmit) o.onSubmit(); } }, general, grid);

    return {
      el: form,
      focus() { const first = fields.url.input; first.focus(); },
      values() {
        const v = {};
        Object.keys(fields).forEach((name) => { v[name] = fields[name].input.value; });
        return v;
      },
      /** Shows server-side validation messages next to their fields; anything else goes to the banner. */
      setErrors(errors, message) {
        Object.keys(fields).forEach((name) => { fields[name].error.textContent = ''; fields[name].input.removeAttribute('aria-invalid'); });
        general.hidden = true;
        general.textContent = '';
        const stray = [];
        Object.keys(errors || {}).forEach((name) => {
          const messages = [].concat(errors[name]);
          if (fields[name]) {
            fields[name].error.textContent = messages.join(' ');
            fields[name].input.setAttribute('aria-invalid', 'true');
          } else stray.push(messages.join(' '));
        });
        if (message) stray.push(message);
        if (stray.length) { general.textContent = stray.join(' '); general.hidden = false; }
        const bad = Object.keys(fields).find((name) => fields[name].input.getAttribute('aria-invalid') === 'true');
        if (bad) fields[bad].input.focus();
      },
    };
  }

  /** The JSON body for POST /api/links from the form values. */
  function createBody(v) {
    const body = { url: v.url.trim() };
    if (v.slug && v.slug.trim()) body.slug = v.slug.trim();
    if (v.title.trim()) body.title = v.title.trim();
    const tags = parseTags(v.tags);
    if (tags.length) body.tags = tags;
    if (v.expiresAt) body.expiresAt = fromLocalInput(v.expiresAt);
    if (v.maxClicks !== '') body.maxClicks = Number(v.maxClicks);
    return body;
  }

  /** A JSON merge patch with only what changed: null clears title, tags, expiry and click limit. */
  function patchBody(v, link) {
    const patch = {};
    if (v.url.trim() !== link.url) patch.url = v.url.trim();
    const title = v.title.trim();
    if (title !== (link.title || '')) patch.title = title || null;
    const tags = parseTags(v.tags);
    if (tags.join(',') !== link.tags.join(',')) patch.tags = tags;
    const before = link.expiresAt ? toLocalInput(link.expiresAt) : '';
    if (v.expiresAt !== before) patch.expiresAt = v.expiresAt ? fromLocalInput(v.expiresAt) : null;
    const limit = v.maxClicks === '' ? null : Number(v.maxClicks);
    if (limit !== (link.maxClicks == null ? null : link.maxClicks)) patch.maxClicks = limit;
    return patch;
  }

  function describeFailure(error) {
    if (error.status === 429) return `Too many requests. Try again in ${error.retryAfter || 60} seconds.`;
    return error.message;
  }

  /* ───────────────────────── create dialog and the quick composer ───────────────────────── */

  /** The full form in a dialog: slug, title, tags, expiry and click limit. `values.url` pre-fills the destination. */
  function openCreate(values) {
    let busy = false;
    const form = linkForm({ values, onSubmit: () => submit() });
    const dialog = Aurora.dialog({
      title: 'New short link', size: 'md', content: form.el,
      actions: [
        { label: 'Cancel', kind: 'ghost' },
        { label: 'Create link', kind: 'primary', closes: false, onClick: () => { submit(); return false; } },
      ],
    });
    const submitButton = $('.a-btn--primary', dialog.el);
    setTimeout(() => form.focus(), 0);

    async function submit() {
      if (busy) return;
      busy = true;
      submitButton.disabled = true;
      form.setErrors({});
      try {
        const link = await api.post('/api/links', createBody(form.values()));
        dialog.close();
        created(link);
      } catch (e) {
        if (e.status === 409) form.setErrors({ slug: [e.message] });
        else if (e.status === 400) form.setErrors(e.fieldErrors, Object.keys(e.fieldErrors).length ? '' : e.message);
        else form.setErrors({}, describeFailure(e));
      } finally {
        busy = false;
        submitButton.disabled = false;
      }
    }
  }

  /** After a link was created: say so, refresh the list and open the link's drawer. */
  function created(link) {
    Aurora.toast(`Created /${link.slug}`, { kind: 'good' });
    refreshLinks();
    location.hash = `#/links/${encodeURIComponent(link.slug)}`;
  }

  /**
   * The primary input of the Links and first-run screens: paste a URL, press Enter, get a short link with a random slug.
   * "More options" opens the full form with the URL carried over.
   */
  function shortenComposer() {
    let busy = false;
    const id = Aurora.uid('quick');
    const input = el('input', {
      class: 'a-composer__input', id: `${id}-url`, name: 'url', type: 'url', maxlength: 2048, autocomplete: 'off', spellcheck: 'false',
      placeholder: 'Paste a long URL to shorten it', 'aria-label': 'Destination URL', 'aria-describedby': `${id}-error`,
    });
    const error = el('div', { class: 'a-field__error', id: `${id}-error`, role: 'alert', hidden: true });
    const send = el('button', { class: 'a-btn a-btn--primary a-btn--sm', type: 'submit' }, 'Shorten', icon('arrow-right', 14));
    const show = (message) => {
      error.textContent = message;
      error.hidden = !message;
      if (message) input.setAttribute('aria-invalid', 'true'); else input.removeAttribute('aria-invalid');
    };
    const form = el('form', { class: 'a-composer c-composer', novalidate: true, onsubmit: (event) => { event.preventDefault(); submit(); } },
      input,
      el('div', { class: 'a-composer__bar' },
        el('span', { class: 'c-composer__hint' }, icon('link', 14), 'Press Enter for a random 7-character slug'),
        el('span', { class: 'a-spacer' }),
        button('More options', { kind: 'ghost', small: true, onClick: () => openCreate({ url: input.value.trim() }) }),
        send),
      error);

    async function submit() {
      if (busy) return;
      const url = input.value.trim();
      if (!url) { show('Enter a URL first.'); input.focus(); return; }
      busy = true;
      send.disabled = true;
      show('');
      try {
        const link = await api.post('/api/links', { url });
        input.value = '';
        created(link);
      } catch (e) {
        show(e.status === 400 && e.fieldErrors.url ? [].concat(e.fieldErrors.url).join(' ') : describeFailure(e));
      } finally {
        busy = false;
        send.disabled = false;
      }
    }

    return { el: form, focus() { input.focus(); } };
  }

  async function confirmDelete(link, afterDelete) {
    const ok = await Aurora.confirm({
      title: `Delete /${link.slug}?`,
      message: `This removes the link and its ${fmt.int(link.clicks)} recorded clicks. Anyone following ${link.shortUrl} will get a 404. This cannot be undone.`,
      confirmLabel: 'Delete link', kind: 'danger',
    });
    if (!ok) return false;
    try {
      await api.del(`/api/links/${encodeURIComponent(link.slug)}`);
    } catch (e) {
      Aurora.toast(`Could not delete /${link.slug}: ${describeFailure(e)}`, { kind: 'critical' });
      return false;
    }
    unregisterLink(link.slug);
    Aurora.toast(`Deleted /${link.slug}`, { kind: 'good' });
    if (afterDelete) afterDelete();
    refreshLinks();
    return true;
  }

  /* ───────────────────────── link drawer ───────────────────────── */

  let drawer = null;

  function closeDrawer() {
    const open = drawer;
    drawer = null;
    if (open) open.dialog.close();
  }

  function openDrawer(slug) {
    if (drawer && drawer.slug === slug) return;
    closeDrawer();

    const content = el('div', { class: 'c-drawer' });
    const dialog = Aurora.dialog({
      title: `/${slug}`, drawer: true, content, actions: [{ label: 'Close', kind: 'ghost' }],
      onClose: () => {
        if (drawer && drawer.dialog === dialog) drawer = null;
        if (parseHash().arg === slug) { history.replaceState(null, '', '#/links'); syncCrumbs(); }
      },
    });
    const foot = $('.a-dialog__foot', dialog.el);
    drawer = { slug, dialog };
    let current = null;
    let statsRange = '7d';
    let statsSeq = 0;

    function setFoot(buttons) { clear(foot); buttons.forEach((b) => foot.appendChild(b)); }

    async function load() {
      clear(content);
      Aurora.skeleton(content, { lines: 6, height: '18px' });
      try {
        current = await api.get(`/api/links/${encodeURIComponent(slug)}`);
      } catch (e) {
        clear(content);
        if (e.status === 404) Aurora.empty(content, { icon: 'link', title: 'This link does not exist', text: `There is no link with the slug /${slug}. It may have been deleted.` });
        else errorState(content, e, load);
        setFoot([button('Close', { kind: 'ghost', onClick: closeDrawer })]);
        return;
      }
      registerLink(current);
      showView();
    }

    function showView() {
      const link = current;
      clear(content);
      content.appendChild(el('section', { class: 'a-card c-short', 'aria-label': 'Short URL' },
        el('div', { class: 'a-eyebrow' }, 'Short URL'),
        el('div', { class: 'c-short__row' },
          el('span', { class: 'c-short__text' }, link.shortUrl),
          button(null, { icon: 'copy', small: true, kind: 'ghost', aria: 'Copy short URL', title: 'Copy short URL', onClick: () => Aurora.copy(link.shortUrl, { label: 'Short URL copied' }) })),
        el('div', { class: 'a-row c-short__meta' }, statusBadge(link.status), el('span', { class: 'a-faint c-small' }, `${plural(link.clicks, 'click', 'clicks')} in total`))));

      Aurora.kv(content, [
        ['Destination', el('a', { class: 'c-ext', href: link.url, target: '_blank', rel: 'noopener noreferrer' }, link.url)],
        ['Title', link.title],
        ['Tags', link.tags.length ? el('span', { class: 'c-tags' }, link.tags.map((t) => el('span', { class: 'a-tag' }, t))) : null],
        ['Created', when(link.createdAt)],
        ['Expires', link.expiresAt ? when(link.expiresAt) : 'Never'],
        ['Click limit', link.maxClicks ? `${fmt.int(link.clicks)} of ${fmt.int(link.maxClicks)} used` : 'No limit'],
      ]);
      if (link.maxClicks) {
        Aurora.meter(content, { value: link.clicks, max: link.maxClicks, label: 'Click budget', format: (v) => `${fmt.int(v)} / ${fmt.int(link.maxClicks)}`, thresholds: [{ at: 0.7, kind: 'warn' }, { at: 1, kind: 'critical' }] });
      }

      const qrSlug = encodeURIComponent(link.slug);
      content.appendChild(el('section', { class: 'a-card c-qr', 'aria-label': 'QR code' },
        el('img', { class: 'c-qr__img', src: `/qr/${qrSlug}.svg?size=240`, alt: `QR code that opens ${link.shortUrl}`, width: 168, height: 168 }),
        el('div', { class: 'c-qr__meta' },
          el('div', { class: 'a-h3' }, 'QR code'),
          el('p', { class: 'a-muted c-small' }, 'A scan opens the short URL, so it counts as a click.'),
          el('a', { class: 'a-btn a-btn--sm', href: `/qr/${qrSlug}.svg?size=1024`, download: `${link.slug}.svg` }, icon('download', 15), 'Download SVG'))));

      // The range switch is built once; changing it only replaces the figures under it, so keyboard focus stays on the switch.
      const rangeSlot = el('span');
      const statsBody = el('div', { class: 'a-stack' });
      content.appendChild(el('section', { class: 'a-stack', 'aria-label': 'Performance' },
        el('div', { class: 'a-row c-between' }, el('h3', { class: 'a-h3' }, 'Performance'), rangeSlot), statsBody));
      const options = rangeOptions(statsRange, (value) => { statsRange = value; loadStats(statsBody); });
      nameItems(Aurora.seg(rangeSlot, options), options.items);
      loadStats(statsBody);

      setFoot([
        button('Delete', { kind: 'danger', small: true, icon: 'trash', onClick: () => confirmDelete(link, closeDrawer) }),
        el('span', { class: 'a-spacer' }),
        button('Edit', { small: true, onClick: showEdit }),
        button('Close', { kind: 'ghost', small: true, onClick: closeDrawer }),
      ]);
    }

    async function loadStats(body) {
      const id = ++statsSeq;
      clear(body);
      Aurora.skeleton(body, { lines: 4, height: '16px' });
      let stats;
      try {
        stats = await api.get(`/api/links/${encodeURIComponent(slug)}/stats`, { range: statsRange });
      } catch (e) {
        if (id !== statsSeq) return;
        clear(body);
        errorState(body, e, () => loadStats(body));
        return;
      }
      if (id !== statsSeq) return;
      clear(body);
      const range = rangeById(statsRange);
      const kpis = el('div', { class: 'c-mini-kpis' });
      body.appendChild(kpis);
      const deltaOf = (c) => (c.previous > 0 ? { value: (c.inRange - c.previous) / c.previous, period: `vs ${range.period}`, good: 'up' } : null);
      const tile = (label, c, spark) => Aurora.stat(kpis, { label, value: c.inRange, delta: deltaOf(c), spark });
      tile('Clicks', stats.clicks, stats.series.map((p) => p.clicks));
      tile('Visitors', stats.visitors, stats.series.map((p) => p.visitors));
      AC.card(body, { title: 'Clicks over time', subtitle: range.label, render: (b) => AC.line(b, lineOptions(stats, range, { height: 200 })) });
      AC.card(body, { title: 'Referrers', subtitle: 'Where the clicks came from', render: (b) => rankedBars(b, { data: barData(stats.referrers.slice(0, 6), stats.clicks.inRange), labelHeader: 'Referrer', title: 'Referrers' }) });
    }

    function showEdit() {
      const link = current;
      const form = linkForm({ link, onSubmit: () => save() });
      clear(content);
      content.appendChild(el('div', { class: 'c-edit-head' }, el('div', { class: 'a-eyebrow' }, 'Editing'), el('div', { class: 'c-short__text' }, link.shortUrl), el('p', { class: 'a-faint c-small' }, 'The slug cannot be changed. Empty fields clear the value.')));
      content.appendChild(form.el);
      const save = async () => {
        const patch = patchBody(form.values(), link);
        if (!Object.keys(patch).length) { Aurora.toast('Nothing to change.'); showView(); return; }
        saveButton.disabled = true;
        form.setErrors({});
        try {
          current = await api.patch(`/api/links/${encodeURIComponent(slug)}`, patch);
          Aurora.toast(`Saved /${slug}`, { kind: 'good' });
          refreshLinks();
          showView();
        } catch (e) {
          form.setErrors(e.status === 400 ? e.fieldErrors : {}, e.status === 400 && Object.keys(e.fieldErrors).length ? '' : describeFailure(e));
        } finally {
          saveButton.disabled = false;
        }
      };
      const saveButton = button('Save changes', { kind: 'primary', small: true, onClick: save });
      setFoot([el('span', { class: 'a-spacer' }), button('Cancel', { kind: 'ghost', small: true, onClick: showView }), saveButton]);
      setTimeout(() => form.focus(), 0);
    }

    load();
  }

  /* ───────────────────────── Overview ───────────────────────── */

  const viewState = { range: '7d' };

  /** First run, with no links at all: the home screen of the app instead of a wall of zeros. */
  function welcome() {
    const composer = shortenComposer();
    const starters = [
      ['blue', 'plus', 'New link', 'Slug, title, tags, expiry and click limit', () => openCreate()],
      ['violet', 'book', 'API reference', 'Every endpoint, with examples', '/docs'],
      ['green', 'command', 'Command palette', 'Jump to a view, a link or an action', () => Aurora.cmdk.open()],
    ];
    return el('section', { class: 'c-welcome', 'aria-label': 'Getting started' },
      el('div', { class: 'a-hero' },
        el('p', { class: 'a-greet' }, 'No links yet'),
        el('h1', { class: 'a-headline c-title', tabindex: '-1' }, 'Shorten your first link'),
        el('p', { class: 'a-lede' }, 'Paste a URL below. As soon as someone follows the short link, clicks, visitors and referrers show up here, live.')),
      el('div', { class: 'c-welcome__body a-stack' },
        composer.el,
        el('div', { class: 'a-cards' }, starters.map(([tile, glyph, title, caption, target]) => {
          const inner = [
            el('span', { class: 'a-linkcard__top' }, el('span', { class: `a-tile a-tile--${tile}` }, icon(glyph, 16)), el('span', { class: 'a-linkcard__title' }, title)),
            el('span', { class: 'a-linkcard__foot' }, el('span', caption), icon('arrow-right', 13)),
          ];
          return typeof target === 'string'
            ? el('a', { class: 'a-card a-card--link', href: target, target: '_blank', rel: 'noopener noreferrer' }, inner)
            : el('button', { class: 'a-card a-card--link', type: 'button', onclick: target }, inner);
        }))));
  }

  function OverviewView(root, ui) {
    let destroyed = false;
    let loadId = 0;
    let data = null;
    let built = false;
    let welcomeBlock = null;
    let grid = null;
    let refreshTimer = null;
    const tiles = {};
    const charts = {};
    const cleanups = [];
    const refreshPill = pill('refresh', '', { title: 'Refresh', aria: 'Refresh', onClick: () => load(true) });
    const linksPill = pill('link', '–', { count: true });
    const clicksPill = pill('trending-up', '–', { count: true });

    const head = el('div', { class: 'a-pagehead' },
      el('div', null, el('h1', { class: 'a-h2 c-title', tabindex: '-1' }, 'Overview'), el('p', { class: 'a-pagehead__sub' }, 'Clicks, visitors and where they come from. Visitors are counted per day and never identified.')));
    root.appendChild(head);
    const rangeSwitch = ui.views(rangeOptions(viewState.range, (id) => { viewState.range = id; load(false, true); }));

    const stage = el('div');
    root.appendChild(stage);
    const skeleton = el('div', { class: 'a-grid' },
      el('div', { class: 'a-card c-skel c-kpi-hero' }, Aurora.skeleton(null, { lines: 3, height: '22px' })),
      el('div', { class: 'a-card c-skel a-span-8' }, Aurora.skeleton(null, { lines: 5, height: '24px' })),
      el('div', { class: 'a-card c-skel a-span-4' }, Aurora.skeleton(null, { lines: 5, height: '24px' })));
    stage.appendChild(skeleton);

    const emptyStats = () => ({ range: { bucket: 'day' }, series: [], clicks: { inRange: 0, previous: 0 } });

    function build() {
      clear(stage);
      welcomeBlock = welcome();
      welcomeBlock.hidden = true;
      stage.appendChild(welcomeBlock);
      grid = el('div', { class: 'a-grid' });
      stage.appendChild(grid);

      const stat = (key, label, o, span) => {
        tiles[key] = Aurora.stat(grid, Object.assign({ label, value: 0 }, o));
        tiles[key].el.classList.add(span);
      };
      stat('clicks', 'Clicks', { hero: true }, 'c-kpi-hero');
      stat('visitors', 'Visitors', {}, 'c-kpi-small');
      stat('links', 'Active links', {}, 'c-kpi-small');
      stat('perVisitor', 'Clicks per visitor', { format: (v) => fmt.number(v, 2) }, 'c-kpi-small');
      stat('peak', 'Busiest bucket', {}, 'c-kpi-small');

      const card = (key, o, span) => {
        charts[key] = AC.card(grid, o);
        charts[key].el.classList.add(span);
      };
      card('line', { title: 'Clicks over time', subtitle: rangeById(viewState.range).label, render: (body) => AC.line(body, lineOptions(emptyStats(), rangeById(viewState.range), { height: 300 })) }, 'a-span-8');
      card('top', {
        title: 'Top links', subtitle: 'Select one to open it',
        render: (body) => rankedBars(body, { data: [], labelHeader: 'Link', title: 'Top links', onSelect: (d) => { location.hash = `#/links/${encodeURIComponent(d.id)}`; } }),
      }, 'a-span-4');
      card('referrers', { title: 'Referrers', subtitle: 'Hosts only, never full URLs', render: (body) => rankedBars(body, { data: [], labelHeader: 'Referrer', title: 'Referrers' }) }, 'a-span-4');
      charts.audience = audienceCard(grid);
      charts.audience.el.classList.add('a-span-8');

      ui.pills([refreshPill.el, linksPill.el, clicksPill.el]);
      built = true;
    }

    function apply(stats, animate) {
      const noLinks = stats.links.total === 0;
      head.hidden = noLinks;
      rangeSwitch.el.hidden = noLinks;
      welcomeBlock.hidden = !noLinks;
      grid.hidden = noLinks;
      linksPill.set(plural(stats.links.total, 'link', 'links'));
      clicksPill.set(plural(stats.clicks.inRange, 'click', 'clicks'));
      if (noLinks) return;

      const range = rangeById(viewState.range);
      const deltaOf = (c) => (c.previous > 0 ? { value: (c.inRange - c.previous) / c.previous, period: `vs ${range.period}`, good: 'up' } : null);
      const clicksSeries = stats.series.map((p) => p.clicks);
      const peak = stats.series.reduce((best, p) => (p.clicks > best.clicks ? p : best), { clicks: 0, t: null });
      const hourly = stats.range.bucket === 'hour';

      tiles.clicks.update({ value: stats.clicks.inRange, delta: deltaOf(stats.clicks), spark: clicksSeries, hint: `All links · ${range.label.toLowerCase()}` });
      tiles.visitors.update({ value: stats.visitors.inRange, delta: deltaOf(stats.visitors), spark: stats.series.map((p) => p.visitors), hint: 'Unique per day' });
      tiles.links.update({ value: stats.links.active, hint: `of ${fmt.int(stats.links.total)} · ${fmt.int(stats.links.expired)} no longer redirect` });
      tiles.perVisitor.update({
        value: stats.visitors.inRange ? stats.clicks.inRange / stats.visitors.inRange : 0,
        spark: stats.series.map((p) => (p.visitors ? p.clicks / p.visitors : 0)),
        hint: 'Clicks divided by visitors',
      });
      $('.a-stat__label', tiles.peak.el).textContent = hourly ? 'Busiest hour' : 'Busiest day';
      tiles.peak.update({ value: peak.clicks, spark: clicksSeries, hint: peak.t ? (hourly ? fmt.date(peak.t, { time: true }) : `${dayLong.format(new Date(peak.t))} (UTC)`) : 'Nothing yet' });

      $('.a-card__sub', charts.line.el).textContent = `${range.label} · ${hourly ? 'hourly buckets' : 'daily buckets (UTC)'}`;
      charts.line.chart.update(lineOptions(stats, range, { height: 300, animate: !!animate }));
      charts.top.chart.update({ data: stats.topLinks.map((l) => ({ id: l.slug, label: `/${l.slug}`, value: l.clicks, sub: l.title || hostOf(l.url) })), animate: !!animate });
      charts.referrers.chart.update({ data: barData(stats.referrers, stats.clicks.inRange), animate: !!animate });
      charts.audience.chart.update(stats);
      stats.topLinks.forEach((l) => registerLink({ slug: l.slug, title: l.title, url: l.url }));
    }

    async function load(manual, rangeChanged) {
      const id = ++loadId;
      root.classList.add('a-refreshing');
      try {
        const stats = await api.get('/api/stats/overview', { range: viewState.range });
        if (destroyed || id !== loadId) return;
        const first = !built;
        if (first) build();
        data = stats;
        apply(stats, first || rangeChanged || manual);
      } catch (e) {
        if (destroyed || id !== loadId) return;
        if (!built) {
          clear(stage);
          const holder = el('div', { class: 'a-card' });
          stage.appendChild(holder);
          errorState(holder, e, () => { clear(stage); stage.appendChild(skeleton); load(); });
        } else Aurora.toast(describeFailure(e), { kind: 'critical' });
      } finally {
        if (id === loadId) root.classList.remove('a-refreshing');
      }
    }

    // Live: every click ticks the headline number up at once; the full numbers are re-read a few seconds later.
    cleanups.push(live.onClick(() => {
      if (!data || !built) return;
      data.clicks.inRange += 1;
      tiles.clicks.update({ value: data.clicks.inRange });
      clicksPill.set(plural(data.clicks.inRange, 'click', 'clicks'));
      clearTimeout(refreshTimer);
      refreshTimer = setTimeout(() => load(), 4000);
    }));
    const interval = setInterval(() => { if (!document.hidden) load(); }, 60000);
    load();

    return {
      destroy() {
        destroyed = true;
        clearTimeout(refreshTimer);
        clearInterval(interval);
        cleanups.forEach((fn) => fn());
        Object.keys(charts).forEach((key) => { const chart = charts[key].chart; if (chart && chart.destroy) chart.destroy(); });
      },
      /** Called by the command palette: moves the range switch of the sub bar, which reloads the figures. */
      setRange(id) { rangeSwitch.select(id); },
    };
  }

  /* ───────────────────────── Links ───────────────────────── */

  const linksState = { query: '', status: '', tag: '', sort: 'newest', offset: 0 };
  let linksView = null;

  function refreshLinks() { if (linksView) linksView.reload(); }

  function LinksView(root, ui) {
    const st = linksState;
    let destroyed = false;
    let loadId = 0;
    let searchTimer = null;

    const search = el('input', {
      class: 'a-input c-search', type: 'search', placeholder: 'Search slug, title or URL', 'aria-label': 'Search links', maxlength: 200, autocomplete: 'off',
      oninput: (event) => { clearTimeout(searchTimer); searchTimer = setTimeout(() => { st.query = event.target.value.trim(); st.offset = 0; load(); }, 250); },
    });
    search.value = st.query;
    const sortSelect = el('select', { class: 'a-select c-sort', 'aria-label': 'Sort links', onchange: (event) => { st.sort = event.target.value; st.offset = 0; load(); } },
      SORTS.map((s) => el('option', { value: s.id }, s.label)));
    sortSelect.value = st.sort;
    const tagChip = el('span', { class: 'c-tagchip-slot' });
    const tableHost = el('div', { class: 'a-tablewrap' });
    const pager = el('div', { class: 'a-pager', hidden: true });
    const countPill = pill('link', '–', { count: true });

    // The status filter is the view switch of the sub bar; the tag filter, a removable chip, lives next to the search box.
    const statusSwitch = ui.views({
      items: STATUSES.map((s) => ({ id: s.id, label: s.label })), active: statusByValue(st.status).id, ariaLabel: 'Filter by status',
      onChange: (id) => { st.status = STATUSES.find((s) => s.id === id).status; st.offset = 0; load(); },
    });
    ui.pills([countPill.el]);

    function drawTag() {
      clear(tagChip);
      if (st.tag) {
        tagChip.appendChild(el('button', { class: 'a-chip', type: 'button', 'aria-pressed': 'true', title: 'Remove the tag filter', onclick: () => { st.tag = ''; st.offset = 0; drawTag(); load(); } }, `tag: ${st.tag}`, icon('x', 13)));
      }
    }
    drawTag();

    root.appendChild(el('div', { class: 'a-pagehead' },
      el('div', null, el('h1', { class: 'a-h2 c-title', tabindex: '-1' }, 'Links'), el('p', { class: 'a-pagehead__sub' }, 'Create, search and manage short links. Select a row for its QR code and statistics.'))));
    root.appendChild(shortenComposer().el);
    root.appendChild(el('section', { class: 'a-card a-card--flush', 'aria-label': 'Short links' },
      el('div', { class: 'a-toolbar c-toolbar' }, el('span', { class: 'c-search-wrap' }, icon('search', 16), search), sortSelect, tagChip),
      tableHost, pager));
    Aurora.skeleton(tableHost, { lines: 5, height: '20px' }).classList.add('c-skel-pad');

    function sortableHeader(label, col, key, ascId, descId) {
      const current = SORTS.find((s) => s.id === st.sort);
      const dir = current.sort === key ? current.dir : null;
      return el('th', { scope: 'col', 'data-col': col, 'aria-sort': dir ? (dir === 'asc' ? 'ascending' : 'descending') : null, 'data-align': col === 'clicks' ? 'right' : 'left' },
        el('button', {
          type: 'button',
          onclick: () => { st.sort = dir === 'desc' ? ascId : descId; st.offset = 0; sortSelect.value = st.sort; load(); },
        }, label, dir ? icon(dir === 'asc' ? 'arrow-up' : 'arrow-down', 12) : null));
    }

    function rowFor(link) {
      const open = () => { location.hash = `#/links/${encodeURIComponent(link.slug)}`; };
      const tr = el('tr', {
        'data-clickable': '', tabindex: '0',
        onclick: open,
        onkeydown: (event) => { if (event.target === tr && (event.key === 'Enter' || event.key === ' ')) { event.preventDefault(); open(); } },
      });
      tr.appendChild(el('td', { 'data-col': 'link' },
        el('div', { class: 'c-link' }, el('span', { class: 'c-slug' }, `/${link.slug}`), link.title ? el('span', { class: 'c-link__title' }, link.title) : null)));
      tr.appendChild(el('td', { 'data-col': 'url', title: link.url }, el('span', { class: 'c-url' }, hostOf(link.url), el('span', { class: 'a-faint' }, new URL(link.url).pathname.replace(/^\/$/, '')))));
      tr.appendChild(el('td', { 'data-col': 'tags' }, el('span', { class: 'c-tags' }, link.tags.slice(0, 3).map((t) => tagButton(t, (tag) => { st.tag = tag; st.offset = 0; drawTag(); load(); })), link.tags.length > 3 ? el('span', { class: 'a-faint c-small' }, `+${link.tags.length - 3}`) : null)));
      tr.appendChild(el('td', { 'data-col': 'clicks', 'data-align': 'right', class: 'a-num' }, link.maxClicks ? `${fmt.int(link.clicks)} / ${fmt.int(link.maxClicks)}` : fmt.int(link.clicks)));
      tr.appendChild(el('td', { 'data-col': 'created', title: fmt.date(link.createdAt, { time: true }) }, el('span', { class: 'a-muted' }, fmt.relative(link.createdAt))));
      tr.appendChild(el('td', { 'data-col': 'status' }, statusBadge(link.status)));
      tr.appendChild(el('td', { 'data-col': 'actions', 'data-align': 'right' }, el('span', { class: 'c-row-actions' },
        button(null, { icon: 'copy', small: true, kind: 'ghost', aria: `Copy ${link.shortUrl}`, title: 'Copy short URL', onClick: (event) => { event.stopPropagation(); Aurora.copy(link.shortUrl, { label: 'Short URL copied' }); } }),
        button(null, { icon: 'trash', small: true, kind: 'ghost', aria: `Delete /${link.slug}`, title: 'Delete', onClick: (event) => { event.stopPropagation(); confirmDelete(link); } }))));
      return tr;
    }

    function render(page) {
      clear(tableHost);
      page.items.forEach((link) => registerLink(link));
      countPill.set(plural(page.total, 'link', 'links'));
      const filtered = !!(st.query || st.status || st.tag);
      if (!page.items.length) {
        if (filtered) {
          Aurora.empty(tableHost, {
            icon: 'search', title: 'No links match', text: 'Try a different search or clear the filters.',
            action: { label: 'Clear filters', onClick: () => { st.query = ''; st.status = ''; st.tag = ''; st.offset = 0; search.value = ''; statusSwitch.select('all', true); drawTag(); load(); } },
          });
        } else {
          Aurora.empty(tableHost, { icon: 'link', title: 'No links yet', text: 'Paste a URL above to create your first short link. Nothing but the click itself is recorded.' });
        }
        pager.hidden = true;
        return;
      }
      const table = el('table', { class: 'a-table c-links', 'aria-label': 'Short links' },
        el('thead', null, el('tr', null,
          el('th', { scope: 'col', 'data-col': 'link' }, 'Link'),
          el('th', { scope: 'col', 'data-col': 'url' }, 'Destination'),
          el('th', { scope: 'col', 'data-col': 'tags' }, 'Tags'),
          sortableHeader('Clicks', 'clicks', 'clicks', 'least', 'most'),
          sortableHeader('Created', 'created', 'createdAt', 'oldest', 'newest'),
          el('th', { scope: 'col', 'data-col': 'status' }, 'Status'),
          el('th', { scope: 'col', 'data-col': 'actions' }, el('span', { class: 'a-sr-only' }, 'Actions')))),
        el('tbody', null, page.items.map(rowFor)));
      tableHost.appendChild(table);

      const from = st.offset + 1;
      const to = Math.min(page.total, st.offset + page.items.length);
      clear(pager);
      pager.hidden = false;
      pager.appendChild(el('span', { class: 'a-num', 'aria-live': 'polite' }, `${fmt.int(from)}–${fmt.int(to)} of ${fmt.int(page.total)}`));
      pager.appendChild(button('Previous', { small: true, disabled: st.offset === 0, onClick: () => { st.offset = Math.max(0, st.offset - PAGE_SIZE); load(); } }));
      pager.appendChild(button('Next', { small: true, disabled: to >= page.total, onClick: () => { st.offset += PAGE_SIZE; load(); } }));
    }

    async function load() {
      const id = ++loadId;
      tableHost.classList.add('a-refreshing');
      try {
        const sort = SORTS.find((s) => s.id === st.sort);
        const page = await api.get('/api/links', { query: st.query, tag: st.tag, status: st.status, sort: sort.sort, dir: sort.dir, limit: PAGE_SIZE, offset: st.offset });
        if (destroyed || id !== loadId) return;
        if (!page.items.length && st.offset > 0 && page.total > 0) {
          st.offset = Math.floor((page.total - 1) / PAGE_SIZE) * PAGE_SIZE;
          load();
          return;
        }
        render(page);
      } catch (e) {
        if (destroyed || id !== loadId) return;
        clear(tableHost);
        errorState(tableHost, e, load);
        pager.hidden = true;
      } finally {
        if (id === loadId) tableHost.classList.remove('a-refreshing');
      }
    }

    const view = {
      destroy() { destroyed = true; clearTimeout(searchTimer); if (linksView === view) linksView = null; closeDrawer(); },
      reload: load,
      setArg(slug) { if (slug) openDrawer(slug); else closeDrawer(); },
    };
    linksView = view;
    load();
    return view;
  }

  /* ───────────────────────── Live ───────────────────────── */

  function LiveView(root) {
    const cleanups = [];
    let feed = null;
    let placeholder = null;
    const tiles = {};
    const charts = {};

    root.appendChild(el('div', { class: 'a-pagehead' },
      el('div', null, el('h1', { class: 'a-h2 c-title', tabindex: '-1' }, 'Live'),
        el('p', { class: 'a-pagehead__sub' }, 'Every redirect appears here as it happens. The stream carries the link, the referrer host and coarse browser, OS and device families, never an address.')),
      el('div', { class: 'a-pagehead__actions' }, button('Clear', { icon: 'refresh', small: true, onClick: () => { session.reset(); renderAll(true); } }))));

    const grid = el('div', { class: 'a-grid' });
    root.appendChild(grid);
    const stat = (key, label, o, span) => {
      tiles[key] = Aurora.stat(grid, Object.assign({ label, value: 0 }, o));
      tiles[key].el.classList.add(span);
    };
    stat('total', 'Clicks this session', { hero: true }, 'c-kpi-hero');
    stat('rate', 'Per minute', { hint: 'Clicks in the last 60 seconds' }, 'c-kpi-small');
    stat('links', 'Links hit', { hint: 'Different short links' }, 'c-kpi-small');
    stat('mobile', 'Mobile share', { hint: 'Of this session\'s clicks', format: 'pct' }, 'c-kpi-small');
    stat('referrers', 'Referrers seen', { hint: 'Different referrer hosts' }, 'c-kpi-small');

    const rateLine = (now) => {
      const values = session.series(now);
      return { series: [{ id: 'clicks', name: 'Clicks', color: 1, values }], y: countAxis(values.map((p) => p.y)), animate: false };
    };
    charts.rate = AC.card(grid, {
      title: 'Clicks per 10 seconds', subtitle: 'The last five minutes',
      render: (body) => AC.line(body, Object.assign({ x: { type: 'time', label: 'Time' }, area: true, legend: 'never', height: 240, valueFormat: fmt.int, title: 'Clicks per 10 seconds' }, rateLine(Date.now()))),
    });
    charts.rate.el.classList.add('a-span-8');
    charts.top = AC.card(grid, { title: 'Top links', subtitle: 'This session', render: (body) => rankedBars(body, { data: [], labelHeader: 'Link', title: 'Top links this session', emptyText: 'Waiting for the first click' }) });
    charts.top.el.classList.add('a-span-4');

    const feedCard = el('section', { class: 'a-card a-card--flush a-reveal', 'aria-label': 'Live feed' },
      el('div', { class: 'a-card__head' }, el('div', null, el('div', { class: 'a-card__title' }, 'Live feed'), el('div', { class: 'a-card__sub' }, 'Newest first, the last 40 clicks'))));
    const feedBody = el('div', { class: 'a-card__body', 'aria-live': 'off' });
    feedCard.appendChild(feedBody);
    root.appendChild(feedCard);
    Aurora.reveal(feedCard);

    function rowFor(entry) {
      const c = entry.click;
      return el('div', { class: 'c-feed-row' },
        el('span', { class: 'a-faint a-num c-feed-row__time' }, clock(entry.at)),
        el('span', { class: 'a-tag' }, `/${c.slug}`),
        el('span', { class: 'c-feed-row__client' }, `${c.browser} · ${c.os}`),
        el('span', { class: 'a-faint c-feed-row__device' }, c.device),
        el('span', { class: 'a-spacer' }),
        el('span', { class: 'a-faint c-feed-row__ref' }, c.referrer));
    }

    function showPlaceholder() {
      if (placeholder) return;
      placeholder = Aurora.empty(feedBody, {
        icon: 'bolt', title: live.state === 'live' ? 'Waiting for the first click' : 'Not connected yet',
        text: 'Open a short link in another tab, or run the seed script from the examples folder, and the click shows up here.',
      });
    }

    function renderAll(reset) {
      if (reset || !feed) {
        clear(feedBody);
        placeholder = null;
        feed = Aurora.feed(feedBody, { max: 40 });
        session.feed.slice().reverse().forEach((entry) => feed.push(rowFor(entry)));
        if (!session.feed.length) showPlaceholder();
      }
      tick();
    }

    function tick() {
      const now = Date.now();
      const minutes = Math.max(1, (now - session.since) / 60000);
      tiles.total.update({ value: session.total, hint: `Since ${clock(new Date(session.since))} · about ${fmt.number(session.total / minutes, 1)} a minute` });
      tiles.rate.update({ value: session.perMinute(now) });
      tiles.links.update({ value: session.distinctLinks() });
      tiles.mobile.update({ value: session.mobileShare() });
      tiles.referrers.update({ value: session.distinctReferrers() });
      charts.rate.chart.update(rateLine(now));
      charts.top.chart.update({ data: session.topLinks(8).map((l) => ({ id: l.slug, label: `/${l.slug}`, value: l.clicks })), animate: false });
    }

    cleanups.push(live.onClick((click) => {
      if (placeholder) { clear(feedBody); placeholder = null; feed = Aurora.feed(feedBody, { max: 40 }); }
      feed.push(rowFor(session.feed[0]));
      tick();
    }));
    cleanups.push(live.onState(() => { if (placeholder) { clear(feedBody); placeholder = null; showPlaceholder(); } }));
    const interval = setInterval(tick, 1000);
    renderAll(true);

    return {
      destroy() {
        clearInterval(interval);
        cleanups.forEach((fn) => fn());
        Object.keys(charts).forEach((key) => { const chart = charts[key].chart; if (chart && chart.destroy) chart.destroy(); });
      },
    };
  }

  /* ───────────────────────── command palette entries for links ───────────────────────── */

  const knownLinks = new Map();

  function registerLink(link) {
    const id = `link:${link.slug}`;
    if (!knownLinks.has(link.slug) && knownLinks.size >= 200) return;
    knownLinks.set(link.slug, true);
    Aurora.cmdk.register([{
      id, label: `/${link.slug}`, group: 'Links', hint: link.title || hostOf(link.url || ''),
      keywords: `${link.title || ''} ${link.url || ''}`, run: () => { location.hash = `#/links/${encodeURIComponent(link.slug)}`; },
    }]);
  }

  function unregisterLink(slug) {
    knownLinks.delete(slug);
    Aurora.cmdk.unregister(`link:${slug}`);
  }

  /* ───────────────────────── page shell: rail, bars and status bar ───────────────────────── */

  let shell = null;

  /** The rail's logo tile: the "c" of the favicon, drawn in the tile's own ink. */
  function brandMark() {
    return Aurora.svg('svg', { viewBox: '0 0 64 64', width: 22, height: 22, fill: 'none', stroke: 'currentColor', 'stroke-width': 7, 'stroke-linecap': 'round', 'aria-hidden': 'true', focusable: 'false' },
      Aurora.svg('path', { d: 'M42 22a13 13 0 1 0 0 20' }));
  }

  /** What a view may put into the shell: the view switch, and the pills at the right end of the sub bar. */
  const ui = {
    views(opts) { return nameItems(shell.setViews(opts), opts.items); },
    pills(nodes) { shell.setSubbarEnd(nodes); },
  };

  function statusBar(version) {
    const external = (href, text) => el('a', { href, target: '_blank', rel: 'noopener noreferrer' }, text);
    return {
      start: [version ? `curto v${version}` : 'curto', external('/docs', 'API reference'), external('/readyz', 'Readiness')],
      end: ['No IP addresses are stored'],
    };
  }

  function buildShell() {
    const initial = parseHash();
    const here = known(initial.name);
    const indicator = Aurora.live(null, { label: 'Live' });
    live.onState((state, text) => indicator.set(state, { live: 'Live', connecting: 'Connecting…', offline: text || 'Offline' }[state]));
    const newLink = el('button', { class: 'a-btn a-btn--primary a-btn--sm c-newlink', type: 'button', 'aria-label': 'New link', onclick: () => openCreate() },
      icon('plus', 15), el('span', { class: 'c-newlink__label' }, 'New link'));
    shell = Aurora.shell({
      brand: { name: 'curto', href: '#/overview', mark: brandMark() },
      rail: [
        ROUTES.map((r) => ({ id: r.id, label: r.label, icon: r.icon, href: `#/${r.id}`, active: r.id === here })),
        'spacer',
        [{ id: 'palette', label: 'Search and commands', icon: 'command', cmdk: true }],
      ],
      crumbs: crumbsFor(here, initial.arg),
      actions: [indicator.el, newLink],
      status: statusBar(''),
    });
  }

  async function showVersion() {
    try {
      const res = await fetch('/healthz', { cache: 'no-store' });
      const body = await res.json();
      shell.setStatus(statusBar(body.version));
    } catch (e) { /* the status bar keeps the plain name when the probe is unreachable */ }
  }

  /* ───────────────────────── router and start-up ───────────────────────── */

  function parseHash() {
    const parts = location.hash.replace(/^#\/?/, '').split('/');
    let arg = null;
    if (parts[1]) { try { arg = decodeURIComponent(parts[1]); } catch (e) { arg = null; } }
    return { name: parts[0] || 'overview', arg };
  }

  const factories = { overview: OverviewView, links: LinksView, live: LiveView };
  const known = (name) => (factories[name] ? name : 'overview');
  let active = null;

  /** The breadcrumbs follow the route; an open link adds its slug. */
  function crumbsFor(name, arg) {
    const label = routeById(name).label;
    return name === 'links' && arg ? ['curto', label, `/${arg}`] : ['curto', label];
  }

  function syncCrumbs() {
    const { name, arg } = parseHash();
    shell.setCrumbs(crumbsFor(known(name), arg));
  }

  function route(userInitiated) {
    const { name, arg } = parseHash();
    const here = known(name);
    if (!active || active.name !== here) {
      if (active) active.instance.destroy();
      // Every view fills the sub bar itself; start from an empty one.
      shell.setViews(null);
      shell.setSubbarEnd([]);
      clear(shell.container);
      const holder = el('div', { class: 'c-view' });
      shell.container.appendChild(holder);
      active = { name: here, instance: factories[here](holder, ui) };
      document.title = `${routeById(here).label} · curto`;
      if (userInitiated) {
        const heading = Array.from(holder.querySelectorAll('.c-title')).find((node) => node.offsetParent !== null);
        if (heading) heading.focus({ preventScroll: true });
      }
    }
    if (active.instance.setArg) active.instance.setArg(here === 'links' ? arg : null);
    shell.setActive(here);
    shell.setCrumbs(crumbsFor(here, arg));
  }

  function restart() { if (active) { active.instance.destroy(); active = null; } route(false); }

  function registerCommands() {
    const go = (hash) => () => { location.hash = hash; };
    const openTab = (path) => () => { window.open(path, '_blank', 'noopener'); };
    const setRange = (id) => () => {
      viewState.range = id;
      if (active && active.name === 'overview') active.instance.setRange(id); else location.hash = '#/overview';
    };
    Aurora.cmdk.register([
      { id: 'go-overview', label: 'Go to Overview', group: 'Navigate', hint: 'view', run: go('#/overview') },
      { id: 'go-links', label: 'Go to Links', group: 'Navigate', hint: 'view', run: go('#/links') },
      { id: 'go-live', label: 'Go to Live', group: 'Navigate', hint: 'view', keywords: 'stream feed realtime', run: go('#/live') },
      { id: 'open-api', label: 'Open the API reference', group: 'Navigate', hint: 'new tab', keywords: 'docs openapi scalar endpoints', run: openTab('/docs') },
      { id: 'open-ready', label: 'Open the readiness probe', group: 'Navigate', hint: 'new tab', keywords: 'health status readyz pipeline', run: openTab('/readyz') },
      { id: 'new-link', label: 'Create a new link', group: 'Actions', hint: 'dialog', keywords: 'add shorten', run: () => openCreate() },
      { id: 'theme', label: 'Toggle light / dark theme', group: 'Actions', keywords: 'appearance mode', run: () => Aurora.theme.toggle() },
      { id: 'key', label: 'Change API key', group: 'Actions', keywords: 'sign in authentication', run: () => { auth.ensure('Enter the new API key. It replaces the one stored for this tab.').then((ok) => { if (ok) restart(); }); } },
      { id: 'forget', label: 'Forget the API key (sign out)', group: 'Actions', keywords: 'logout clear', run: () => { live.stop(); auth.set(''); restart(); } },
    ].concat(RANGES.map((r) => ({ id: `range-${r.id}`, label: `Overview: ${r.label.toLowerCase()}`, group: 'Range', hint: r.id, keywords: 'period time window', run: setRange(r.id) }))));
  }

  async function start() {
    Aurora.init();
    buildShell();
    registerCommands();
    auth.onChange((has) => { live.stop(); if (has) live.start(); });
    if (!auth.has()) await auth.ensure();
    window.addEventListener('hashchange', () => route(true));
    if (auth.has()) live.start();
    route(false);
    showVersion();
  }

  start();
})();
