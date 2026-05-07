class o extends Error {
  constructor() {
    super(...arguments), this.name = "AuthContextUnavailableError";
  }
}
async function l(r, c, e) {
  const i = await r();
  if (!i)
    throw new o("Auth context unavailable");
  const a = i.getOpenApiConfiguration();
  let t;
  try {
    t = await a.token();
  } catch {
    throw new o("Token acquisition failed");
  }
  if (!t || t.trim() === "")
    throw new o("Token acquisition returned empty");
  const s = e.body !== void 0, d = {
    Accept: "application/json",
    Authorization: `Bearer ${t}`,
    ...s ? { "Content-Type": "application/json" } : {}
  }, n = { ...e.headers ?? {} };
  delete n.Authorization, delete n.authorization;
  const h = {
    ...d,
    ...n
  };
  return fetch(`${a.base}${c}`, {
    method: e.method ?? "GET",
    credentials: a.credentials,
    signal: e.signal,
    headers: h,
    body: s ? JSON.stringify(e.body) : void 0
  });
}
export {
  o as A,
  l as a
};
