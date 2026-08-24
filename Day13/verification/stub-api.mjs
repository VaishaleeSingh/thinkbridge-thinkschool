/**
 * A stand-in for the Week-1 QuotesApi, used ONLY to verify the Angular UI.
 *
 * It is not part of the application and the application does not know it exists:
 * quotes-web points at http://localhost:5059, which is where `dotnet run` serves
 * the real API. This process answers on the same address so the UI can be driven
 * in a browser on a machine with no .NET SDK.
 *
 * It mirrors the real contract deliberately and narrowly -- the same routes, the
 * same status codes, the same ProblemDetails and ValidationProblemDetails bodies,
 * the same CORS policy, the same aggregate invariants (name <= 80, max 50 items,
 * no duplicate quote in a collection, author <= 200, text <= 1000). Where it
 * cannot be faithful it fails loudly rather than quietly differing.
 *
 * It also does one thing the real API cannot be asked to do on demand: fail. The
 * /__verify/mode endpoint forces the error, empty and expired-token paths so the
 * UI states that depend on them can actually be exercised rather than assumed.
 */
import { createServer } from 'node:http';

const PORT = 5059;
const ALLOWED_ORIGINS = ['http://localhost:4200'];
const ACCESS_TOKEN_LIFETIME_SECONDS = 900;

/**
 * @type {{
 *   quotes: 'ok'|'empty'|'error',
 *   collections: 'ok'|'empty'|'error',
 *   expireCount: number,
 *   deleteFails: boolean,
 *   collectionCreateFails: boolean
 * }}
 */
const mode = {
  quotes: 'ok',
  collections: 'ok',
  /**
   * How many of the next authenticated requests to answer with 401.
   *
   * A count rather than a boolean, because one of the behaviours worth testing is
   * what happens when SEVERAL requests hit an expired token at once: the client
   * must send exactly one refresh, since this API treats a re-presented refresh
   * token as theft and revokes the whole family. A boolean could only ever
   * expire one request, so the case that matters could not be reached.
   */
  expireCount: 0,

  // Forces DELETE /api/quotes/{id} to fail, so the "a failed action must not
  // clear the list" behaviour can actually be observed. A real API refuses a
  // delete on a quote you do not own; this reproduces that without needing a
  // second account.
  deleteFails: false,

  // Forces the next POST /api/collections to fail with a 500 instead of
  // creating anything -- see the handler for why this exists.
  collectionCreateFails: false,

  /**
   * Per-id artificial latency for GET /api/quotes/{id}, as { [id]: milliseconds }.
   *
   * The one thing the detail page has to get right is what happens when two
   * requests overlap -- open quote A, then quote B before A answers -- and that
   * cannot be provoked against a healthy API: this endpoint answers in single
   * digit milliseconds, so the second request is always issued after the first
   * has already landed and the interleave never happens. Slowing ONE id lets the
   * responses be forced to arrive in the opposite order to the clicks, which is
   * the only way to observe whether the guard in QuoteDetailStore actually
   * works rather than merely reading it and believing it.
   */
  quoteDetailDelayMs: {},
};

const users = new Map(); // email -> { id, password }
const refreshTokens = new Map(); // token -> userId
let nextUserId = 1;

const quotes = [];
const collections = [];
let nextQuoteId = 1;
let nextCollectionId = 1;

const SEED = [
  ['Seneca', 'We suffer more often in imagination than in reality.'],
  ['Marcus Aurelius', 'You have power over your mind - not outside events. Realize this, and you will find strength.'],
  ['Epictetus', 'It is not what happens to you, but how you react to it that matters.'],
  ['Simone Weil', 'Attention is the rarest and purest form of generosity.'],
  ['Hannah Arendt', 'The most radical revolutionary will become a conservative the day after the revolution.'],
  ['Iris Murdoch', 'Love is the extremely difficult realisation that something other than oneself is real.'],
  ['Alfred North Whitehead', 'Civilization advances by extending the number of important operations which we can perform without thinking about them.'],
  ['Grace Hopper', 'The most dangerous phrase in the language is: we have always done it this way.'],
  ['Edsger Dijkstra', 'Simplicity is prerequisite for reliability.'],
  ['Barbara Liskov', 'A good abstraction is one that hides the details you do not need.'],
  ['Tony Hoare', 'There are two ways of constructing a software design: make it so simple there are obviously no deficiencies, or make it so complicated that there are no obvious ones.'],
  ['Fred Brooks', 'Adding manpower to a late software project makes it later.'],
  ['Melvin Conway', 'Organizations design systems that mirror their own communication structure.'],
  ['Leslie Lamport', 'A distributed system is one in which the failure of a computer you did not know existed can render your own computer unusable.'],
  ['Donald Knuth', 'Premature optimization is the root of all evil.'],
  ['Rich Hickey', 'Programming is not about typing, it is about thinking.'],
  ['Ward Cunningham', 'Technical debt is the difference between what you know and what you built.'],
  ['Kent Beck', 'Make it work, make it right, make it fast.'],
];

/**
 * Restores the exact starting state. Called at boot and by /__verify/reset, so a
 * verification run is idempotent -- without it a second run finds the user from
 * the first one already registered and takes a different path through the app.
 */
function seed() {
  quotes.length = 0;
  collections.length = 0;
  users.clear();
  refreshTokens.clear();

  nextQuoteId = 1;
  nextCollectionId = 1;
  nextUserId = 1;

  SEED.forEach(([author, text], index) => {
    // The first three belong to someone else, the rest have no recorded creator
    // (the real API's pre-Day-3 rows). Both cases matter: the client must offer
    // delete for the second and not for the first.
    const createdByUserId = index < 3 ? '999' : null;

    // backgroundImageUrl is not decoration here, it is contract. The real
    // Quote.Create always resolves one (Quote.SelectDefaultBackground picks from
    // six bundled files by a hash of author|text), so a quote WITHOUT this field
    // is a shape the API cannot produce. The stub was still omitting it after
    // the field was added, which meant every browser check was rendering quotes
    // the API would never send -- exactly the drift this file's README warns
    // about. Cycled rather than fixed to one value so a page of quotes exercises
    // more than a single image path.
    const backgroundImageUrl = `/quote-backgrounds/mountain-${(index % 6) + 1}.jpg`;

    quotes.push({ id: nextQuoteId++, author, text, backgroundImageUrl, createdByUserId });
  });

  mode.quotes = 'ok';
  mode.collections = 'ok';
  mode.expireCount = 0;
  mode.deleteFails = false;
  mode.collectionCreateFails = false;
  mode.quoteDetailDelayMs = {};
}

seed();

// --- token handling ---------------------------------------------------------
// Shaped exactly like the API's own JWTs (sub / email / scope claims, exp in
// seconds) but unsigned: the client reads the payload and never verifies it, and
// this process is the only thing that has to accept it back.
function base64url(value) {
  return Buffer.from(JSON.stringify(value)).toString('base64url');
}

function issueTokens(user) {
  const exp = Math.floor(Date.now() / 1000) + ACCESS_TOKEN_LIFETIME_SECONDS;
  const payload = {
    sub: String(user.id),
    email: user.email,
    exp,
    scope: ['quotes.read', 'quotes.write', 'quotes.delete', 'collections.read', 'collections.write', 'collections.delete'],
  };

  const accessToken = `${base64url({ alg: 'HS256', typ: 'JWT' })}.${base64url(payload)}.stub-signature`;
  const refreshToken = `refresh-${user.id}-${Math.random().toString(36).slice(2)}`;

  refreshTokens.set(refreshToken, user.id);

  return { accessToken, refreshToken, expiresIn: ACCESS_TOKEN_LIFETIME_SECONDS, tokenType: 'Bearer' };
}

function callerFrom(request) {
  const header = request.headers.authorization;

  if (!header?.startsWith('Bearer ')) {
    return null;
  }

  try {
    const payload = JSON.parse(Buffer.from(header.slice(7).split('.')[1], 'base64url').toString());
    return payload.exp * 1000 > Date.now() ? payload : null;
  } catch {
    return null;
  }
}

// --- helpers ----------------------------------------------------------------
function send(response, status, body, extraHeaders = {}) {
  const headers = { ...extraHeaders };

  if (body === undefined || body === null) {
    response.writeHead(status, headers);
    response.end();
    return;
  }

  const json = JSON.stringify(body);
  response.writeHead(status, { ...headers, 'content-type': 'application/json; charset=utf-8' });
  response.end(json);
}

// Both take the CORS headers, because the real API's CORS middleware runs BEFORE
// its endpoints and stamps every response -- error responses included. Omitting
// them here made a 500 with a readable message arrive at the browser as an opaque
// CORS failure, which the client correctly reported as "could not reach the API".
// Faithful to the contract means faithful on the failure paths too.
function validationProblem(response, errors, corsHeaders = {}) {
  send(
    response,
    400,
    {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      errors,
    },
    corsHeaders,
  );
}

function problem(response, status, title, detail, corsHeaders = {}) {
  send(response, status, { type: 'about:blank', title, status, detail }, corsHeaders);
}

function readBody(request) {
  return new Promise((resolve) => {
    let raw = '';
    request.on('data', (chunk) => (raw += chunk));
    request.on('end', () => {
      try {
        resolve(raw ? JSON.parse(raw) : {});
      } catch {
        resolve({});
      }
    });
  });
}

function collectionListItem(collection) {
  const lastAddedAt = collection.items.length
    ? collection.items[collection.items.length - 1].addedAt
    : null;

  return {
    id: collection.id,
    name: collection.name,
    quoteCount: collection.items.length,
    lastAddedAt,
  };
}

function collectionDetail(collection) {
  return {
    id: collection.id,
    name: collection.name,
    quoteCount: collection.items.length,
    quotes: collection.items.map((item) => {
      const quote = quotes.find((candidate) => candidate.id === item.quoteId);
      return {
        quoteId: item.quoteId,
        author: quote?.author ?? 'Unknown',
        text: quote?.text ?? '',
        addedAt: item.addedAt,
      };
    }),
  };
}

// The API's own timestamp format: UTC, and with no timezone designator -- which
// is exactly the case RelativeTimePipe has to compensate for.
function utcStamp(offsetMs = 0) {
  return new Date(Date.now() + offsetMs).toISOString().replace('Z', '');
}

// --- server -----------------------------------------------------------------
const server = createServer(async (request, response) => {
  const url = new URL(request.url ?? '/', `http://localhost:${PORT}`);
  const origin = request.headers.origin;

  // The same policy as QuotesApi's Day-13 CorsExtensions: named origins, the two
  // headers the SPA sends, the three verbs the API exposes, no credentials.
  const corsHeaders = {};
  if (origin && ALLOWED_ORIGINS.includes(origin)) {
    corsHeaders['access-control-allow-origin'] = origin;
    corsHeaders['vary'] = 'Origin';
  }

  if (request.method === 'OPTIONS') {
    response.writeHead(204, {
      ...corsHeaders,
      'access-control-allow-headers': 'Authorization, Content-Type',
      'access-control-allow-methods': 'GET, POST, DELETE',
      'access-control-max-age': '3600',
    });
    response.end();
    return;
  }

  const path = url.pathname;
  const body = request.method === 'POST' ? await readBody(request) : {};

  // ---- verification control (not part of the real API) ----
  if (path === '/__verify/mode' && request.method === 'POST') {
    Object.assign(mode, body);
    send(response, 200, mode, corsHeaders);
    return;
  }

  if (path === '/__verify/reset' && request.method === 'POST') {
    seed();
    send(response, 200, { reset: true }, corsHeaders);
    return;
  }

  // ---- auth ----
  if (path === '/api/auth/register' && request.method === 'POST') {
    const email = (body.email ?? '').trim();
    const errors = {};

    if (!email || !email.includes('@')) errors.email = ['A valid email address is required.'];
    if (!body.password || body.password.length < 8) errors.password = ['Password must be at least 8 characters.'];

    if (Object.keys(errors).length) return validationProblem(response, errors, corsHeaders);

    if (users.has(email)) {
      return problem(response, 409, 'Email already registered', 'An account already exists for that email address.', corsHeaders);
    }

    const user = { id: nextUserId++, email, password: body.password };
    users.set(email, user);
    send(response, 201, issueTokens(user), corsHeaders);
    return;
  }

  if (path === '/api/auth/login' && request.method === 'POST') {
    const user = users.get((body.email ?? '').trim());

    if (!user || user.password !== body.password) {
      // The real API returns a bare 401 here, with no body and no hint about
      // which half was wrong.
      return send(response, 401, null, corsHeaders);
    }

    send(response, 200, issueTokens(user), corsHeaders);
    return;
  }

  if (path === '/api/auth/refresh' && request.method === 'POST') {
    const userId = refreshTokens.get(body.refreshToken);

    if (!userId) return send(response, 401, null, corsHeaders);

    // Rotation: the presented token dies here, as it does in the real API.
    refreshTokens.delete(body.refreshToken);

    const user = [...users.values()].find((candidate) => candidate.id === userId);

    // A refresh token whose user no longer exists. The real API returns 401 here
    // (it looks the user up and fails the same way); this used to pass undefined
    // into issueTokens, which threw inside an async handler and left the request
    // hanging with no status at all.
    if (!user) return send(response, 401, null, corsHeaders);

    send(response, 200, issueTokens(user), corsHeaders);
    return;
  }

  if (path === '/api/auth/logout' && request.method === 'POST') {
    refreshTokens.delete(body.refreshToken);
    send(response, 204, null, corsHeaders);
    return;
  }

  // Health is unauthenticated in the real API too: an orchestrator probing a
  // container has no token, and a probe that can fail for auth reasons is worse
  // than no probe.
  if (path === '/health') {
    return send(response, 200, { status: 'Healthy', service: 'QuotesApi' }, corsHeaders);
  }

  // ---- everything below needs a bearer token ----
  const caller = callerFrom(request);

  if (!caller) {
    return send(response, 401, null, corsHeaders);
  }

  // Checked only AFTER a valid token was presented: checking it first meant any
  // unauthenticated request in flight could spend the budget before the request
  // under test ever arrived.
  if (mode.expireCount > 0) {
    mode.expireCount -= 1;
    return send(response, 401, null, corsHeaders);
  }

  // ---- quotes ----
  if (path === '/api/quotes' && request.method === 'GET') {
    if (mode.quotes === 'error') {
      return problem(response, 500, 'An unexpected error occurred', 'The database is unavailable.', corsHeaders);
    }

    const page = Number(url.searchParams.get('page'));
    const size = Number(url.searchParams.get('size'));

    if (!Number.isInteger(page) || page < 1 || !Number.isInteger(size) || size < 1 || size > 100) {
      return validationProblem(
        response,
        { page: ['Page must be at least 1.'], size: ['Size must be between 1 and 100.'] },
        corsHeaders,
      );
    }

    const source = mode.quotes === 'empty' ? [] : quotes;
    const start = (page - 1) * size;

    send(response, 200, { page, size, total: source.length, items: source.slice(start, start + size) }, corsHeaders);
    return;
  }

  // GET /api/quotes/{id} -- 200 or 404, matching QuoteEndpointExtensions.
  //
  // Added because the detail page is the first screen to call it. Its absence
  // here is why the endpoint had a typed client method in quotes-api.ts that no
  // verification had ever exercised.
  const detailMatch = /^\/api\/quotes\/(\d+)$/.exec(path);

  if (detailMatch && request.method === 'GET') {
    const id = Number(detailMatch[1]);
    const quote = quotes.find((candidate) => candidate.id === id);

    // Awaited BEFORE answering, and only for the id asked for, so a test can
    // make one specific quote slow and leave every other request fast.
    const delayMs = mode.quoteDetailDelayMs[id] ?? 0;

    if (delayMs > 0) {
      await new Promise((resolve) => setTimeout(resolve, delayMs));
    }

    if (!quote) {
      return send(response, 404, null, corsHeaders);
    }

    send(response, 200, quote, corsHeaders);
    return;
  }

  if (path === '/api/quotes' && request.method === 'POST') {
    const errors = {};
    const author = body.author;
    const text = body.text;

    if (!author || !author.trim()) errors.author = ['Author is required.'];
    else if (author.length > 200) errors.author = ['Author must be 200 characters or less.'];

    if (!text || !text.trim()) errors.text = ['Text is required.'];
    else if (text.length > 1000) errors.text = ['Text must be 1000 characters or less.'];

    if (Object.keys(errors).length) return validationProblem(response, errors, corsHeaders);

    // backgroundImageUrl is mandatory in the real Quote -- Quote.Create always
    // resolves one, even when the caller sends none -- so a created quote
    // missing it is a shape the API cannot produce. Without this, the card that
    // renders the quote just created calls resolveQuoteBackgroundUrl on
    // undefined and throws, which is what turned into "creating a quote closes
    // the dialog and shows it first" failing and every check after it timing
    // out on a card that never rendered.
    const quote = {
      id: nextQuoteId++,
      author: author.trim(),
      text: text.trim(),
      backgroundImageUrl: body.backgroundImageUrl || '/quote-backgrounds/mountain-1.jpg',
      createdByUserId: caller.sub,
    };
    quotes.unshift(quote);
    send(response, 201, quote, { ...corsHeaders, location: `/api/quotes/${quote.id}` });
    return;
  }

  const quoteMatch = /^\/api\/quotes\/(\d+)$/.exec(path);
  if (quoteMatch) {
    const id = Number(quoteMatch[1]);
    const index = quotes.findIndex((quote) => quote.id === id);

    if (index === -1) return send(response, 404, null, corsHeaders);

    // GET is handled earlier, by detailMatch above -- this block only reaches
    // DELETE. Left as a guard rather than an assumption: a future verb added
    // here without reading that block would silently be dead code.

    if (request.method === 'DELETE') {
      if (mode.deleteFails) {
        return problem(
          response,
          403,
          'Forbidden',
          'Only the person who created this quote can delete it.',
          corsHeaders,
        );
      }

      /*
       * The real rule, from MustOwnQuoteHandler, is an equality check with no
       * second branch:
       *
       *     callerId is not null && callerId == resource.CreatedByUserId
       *
       * This used to read `owner !== null && owner !== caller.sub`, which
       * carried the same wrong belief the Angular unit tests carried until this
       * review: that a null owner means "no rule applies", so anyone may
       * delete it. It does not. A null CreatedByUserId simply never equals a
       * real caller id, so the handler never succeeds for it and the real API
       * answers 403. `owner !== caller.sub` alone expresses that correctly for
       * every case -- null included, since null is never equal to a caller's
       * actual id -- with no separate null branch needed.
       */
      const owner = quotes[index].createdByUserId;

      if (owner !== caller.sub) {
        return send(response, 403, null, corsHeaders);
      }

      quotes.splice(index, 1);
      for (const collection of collections) {
        collection.items = collection.items.filter((item) => item.quoteId !== id);
      }
      return send(response, 204, null, corsHeaders);
    }
  }

  // ---- collections ----
  if (path === '/api/collections' && request.method === 'GET') {
    if (mode.collections === 'error') {
      return problem(response, 500, 'An unexpected error occurred', 'The database is unavailable.', corsHeaders);
    }

    const owned =
      mode.collections === 'empty'
        ? []
        : collections.filter((collection) => collection.ownerId === caller.sub);

    send(response, 200, owned.map(collectionListItem), corsHeaders);
    return;
  }

  if (path === '/api/collections' && request.method === 'POST') {
    // Forces the NEXT create to fail with something that is not a validation
    // problem -- a 500, exactly like the real API would return for a genuine
    // server error. This exists to reproduce, on demand, the bug that shipped
    // in CollectionsStore.create(): a non-validation failure was landing on the
    // LOAD-error signal instead of the mutation one, so the whole list vanished
    // behind a full-page error. See verify-ui.mjs's "collections: a non-
    // validation create failure" check.
    if (mode.collectionCreateFails) {
      mode.collectionCreateFails = false;
      return problem(response, 500, 'An unexpected error occurred', 'The database is unavailable.', corsHeaders);
    }

    const name = (body.name ?? '').trim();

    // The real API validates this on the aggregate's constructor, so it arrives
    // as a 400 ProblemDetails with a message and no `errors` dictionary -- the
    // case CollectionsStore.create has a branch for.
    if (!name) return problem(response, 400, 'Bad Request', 'Collection name is required.', corsHeaders);
    if (name.length < 3)
      return problem(response, 400, 'Bad Request', 'Collection name must be between 3 and 80 characters.', corsHeaders);
    if (name.length > 80)
      return problem(response, 400, 'Bad Request', 'Collection name must be between 3 and 80 characters.', corsHeaders);

    const collection = { id: nextCollectionId++, name, ownerId: caller.sub, items: [] };
    collections.push(collection);
    send(response, 201, collectionDetail(collection), corsHeaders);
    return;
  }

  /**
   * Owned by the caller, or undefined. The list endpoint filtered by owner from
   * the start; the detail and item routes did not, which made this stub more
   * permissive than the API it stands in for -- and would have hidden a client
   * that read someone else's collection.
   */
  const ownedCollection = (id) =>
    collections.find((candidate) => candidate.id === Number(id) && candidate.ownerId === caller.sub);

  const collectionMatch = /^\/api\/collections\/(\d+)$/.exec(path);
  if (collectionMatch && request.method === 'GET') {
    const collection = ownedCollection(collectionMatch[1]);
    return collection
      ? send(response, 200, collectionDetail(collection), corsHeaders)
      : send(response, 404, null, corsHeaders);
  }

  // DELETE /api/collections/{id} -- the whole collection, mirroring the real
  // API's new endpoint: 404 if it never existed, 403 if it exists but belongs
  // to someone else (deliberately NOT 404, so the response does not confirm
  // or deny that the id exists to a caller who cannot act on it), 204 on
  // success.
  if (collectionMatch && request.method === 'DELETE') {
    const id = Number(collectionMatch[1]);
    const collection = collections.find((candidate) => candidate.id === id);

    if (!collection) return send(response, 404, null, corsHeaders);
    if (collection.ownerId !== caller.sub) return send(response, 403, null, corsHeaders);

    collections.splice(collections.indexOf(collection), 1);
    return send(response, 204, null, corsHeaders);
  }

  const itemsMatch = /^\/api\/collections\/(\d+)\/items$/.exec(path);
  if (itemsMatch && request.method === 'POST') {
    const collection = ownedCollection(itemsMatch[1]);

    if (!collection) return send(response, 404, null, corsHeaders);

    if (collection.items.length >= 50) {
      return problem(response, 400, 'Bad Request', 'A collection cannot hold more than 50 quotes.', corsHeaders);
    }

    if (collection.items.some((item) => item.quoteId === body.quoteId)) {
      return problem(response, 400, 'Bad Request', 'That quote is already in this collection.', corsHeaders);
    }

    collection.items.push({ quoteId: body.quoteId, addedAt: utcStamp() });

    // The real API returns the Collection AGGREGATE here, not the read model --
    // which is why the client ignores this body and re-reads the detail.
    send(response, 200, { id: collection.id, name: collection.name, items: collection.items }, corsHeaders);
    return;
  }

  const itemMatch = /^\/api\/collections\/(\d+)\/items\/(\d+)$/.exec(path);
  if (itemMatch && request.method === 'DELETE') {
    const collection = ownedCollection(itemMatch[1]);

    if (!collection) return send(response, 404, null, corsHeaders);

    const before = collection.items.length;
    collection.items = collection.items.filter((item) => item.quoteId !== Number(itemMatch[2]));

    return before === collection.items.length
      ? send(response, 404, null, corsHeaders)
      : send(response, 204, null, corsHeaders);
  }

  send(response, 404, null, corsHeaders);
});

server.listen(PORT, () => console.log(`stub QuotesApi listening on http://localhost:${PORT}`));
