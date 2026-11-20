import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { stripTypeScriptTypes } from "node:module";
import { SourceTextModule, SyntheticModule } from "node:vm";
import test from "node:test";

const source = await readFile(new URL("../src/features/discovery/queries.ts", import.meta.url), "utf8");
const requests = [];
const service = { search: async (query) => { requests.push(query); return query; } };
const modules = new Map([
  ["@tanstack/react-query", { infiniteQueryOptions: (options) => options, queryOptions: (options) => options }],
  ["@/services/commercehub-service", { commerceHubService: service }],
  ["@/services/categories-service", { categoriesService: { getCategoryTree: async () => [] } }]
]);
const module = new SourceTextModule(stripTypeScriptTypes(source));
await module.link(async (name) => {
  const values = modules.get(name);
  assert.ok(values, `Unexpected dependency ${name}`);
  return new SyntheticModule(Object.keys(values), function () {
    for (const [key, value] of Object.entries(values)) this.setExport(key, value);
  });
});
await module.evaluate();
const options = module.namespace.infiniteSearchResultsQueryOptions({ fetchQuery: (query) => query.queryFn({}) }, { q: "authority", sort: "price_asc" }, 10);

/*
 * Owner decision X-1b: this case can only pass against an authentic backend
 * page receipt, so it is skipped unless live execution is explicitly opted
 * into. The assertion below is unchanged and still runs, and still fails when
 * the receipt is missing, under SHOPYXI_LIVE_BACKEND=1.
 */
const liveBackendEnabled = process.env.SHOPYXI_LIVE_BACKEND === "1";
const liveBackendSkipReason =
  "requires an authentic PostgreSQL/Mongo backend page receipt; set SHOPYXI_LIVE_BACKEND=1 (with APP17B_BACKEND_PAGE_RECEIPT) to run";

if (!liveBackendEnabled) {
  console.log(`SKIP: ${liveBackendSkipReason}`);
}

test("actual frontend consumer keeps requesting pages until the authoritative total is loaded", { skip: liveBackendEnabled ? false : liveBackendSkipReason }, async () => {
  assert.ok(process.env.APP17B_BACKEND_PAGE_RECEIPT, "Real PostgreSQL/Mongo backend page receipt is required");
  const pages = JSON.parse(await readFile(process.env.APP17B_BACKEND_PAGE_RECEIPT, "utf8"));
  assert.deepEqual(pages.map((page) => page.items.length), [10, 10, 10, 1, 0]);
  assert.ok(pages.every((page) => page.total === 31));
  for (let index = 0; index < 4; index++) {
    assert.equal(options.getNextPageParam(pages[index], pages.slice(0, index + 1)), index < 3 ? index + 2 : undefined);
  }
  assert.equal(options.getNextPageParam(pages[4], pages), undefined);
});

test("the original page-count-as-total defect demonstrably stops this unchanged consumer", () => {
  const page = { items: Array.from({ length: 10 }, (_, id) => ({ id })), total: 10 };
  assert.equal(options.getNextPageParam(page, [page]), undefined);
  assert.equal(options.getNextPageParam({ ...page, total: 31 }, [page]), 2);
});

test("next-page fetch preserves query/filter/sort context and page boundaries", async () => {
  await options.queryFn({ pageParam: 3 });
  assert.deepEqual(requests.at(-1), { q: "authority", sort: "price_asc", page: 3, pageSize: 10 });
});
