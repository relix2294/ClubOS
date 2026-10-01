"use client";

import { useState, type FormEvent } from "react";
import type {
  ClientView,
  PaymentMethod,
  ProductView,
  SaleView,
  StockKind,
  StockMovementView,
} from "@clubos/contracts";
import { useShell } from "@/components/AppShell";
import { ClientPicker } from "@/components/ClientPicker";
import {
  Button,
  Card,
  EmptyState,
  ErrorState,
  Field,
  Loading,
  inputClass,
} from "@/components/ui";
import { apiGet, apiPost } from "@/lib/api";
import {
  formatDateTime,
  formatMoney,
  formatTime,
  parseMoney,
} from "@/lib/format";
import { t } from "@/lib/i18n";
import { usePolling } from "@/lib/usePolling";

function newKey(): string {
  return typeof crypto !== "undefined" && "randomUUID" in crypto
    ? crypto.randomUUID()
    : `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 12)}`;
}

const moneyText = (minor: number) => (minor / 100).toFixed(2).replace(".", ",");

/** Бар / POS: витрина и чек (кассир), чеки смены с возвратом, товары и склад (администратор). */
export default function BarPage() {
  const { location, can } = useShell();
  const [tab, setTab] = useState<"sell" | "catalog">("sell");
  const products = usePolling(
    (s) =>
      apiGet<ProductView[]>(
        `locations/${encodeURIComponent(location.locationId)}/products${can("cash.refund") ? "?all=true" : ""}`,
        s,
      ),
    30_000,
    [location.locationId],
    { topics: ["cash"], locationId: location.locationId },
  );

  if (!can("cash.operate")) {
    return <EmptyState>Раздел недоступен для вашей роли.</EmptyState>;
  }

  return (
    <div className="flex flex-col gap-6">
      <div className="flex flex-wrap items-center gap-3">
        <h1 className="text-2xl font-bold text-slate-900">{t.bar.title}</h1>
        <div className="ml-auto flex gap-2" role="tablist">
          <Button
            variant={tab === "sell" ? "primary" : "secondary"}
            role="tab"
            aria-selected={tab === "sell"}
            onClick={() => setTab("sell")}
          >
            {t.bar.sellTab}
          </Button>
          {can("cash.refund") && (
            <Button
              variant={tab === "catalog" ? "primary" : "secondary"}
              role="tab"
              aria-selected={tab === "catalog"}
              onClick={() => setTab("catalog")}
            >
              {t.bar.catalogTab}
            </Button>
          )}
        </div>
      </div>
      {products.loading && !products.data && <Loading />}
      {products.error && (
        <ErrorState error={products.error} onRetry={products.refresh} />
      )}
      {products.data && tab === "sell" && (
        <SellTab
          products={products.data.filter((p) => p.isActive)}
          onSold={products.refresh}
        />
      )}
      {products.data && tab === "catalog" && (
        <CatalogTab products={products.data} onChanged={products.refresh} />
      )}
    </div>
  );
}

function SellTab({
  products,
  onSold,
}: {
  products: ProductView[];
  onSold: () => void;
}) {
  const { location } = useShell();
  const [cart, setCart] = useState<Record<string, number>>({});
  const [client, setClient] = useState<ClientView | null>(null);
  const [key, setKey] = useState(newKey);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [last, setLast] = useState<SaleView>();
  const sales = usePolling(
    (s) =>
      apiGet<SaleView[]>(
        `locations/${encodeURIComponent(location.locationId)}/sales`,
        s,
      ),
    15_000,
    [location.locationId],
    { topics: ["cash"], locationId: location.locationId },
  );

  const byId = new Map(products.map((p) => [p.productId, p]));
  const lines = Object.entries(cart)
    .filter(([id, qty]) => qty > 0 && byId.has(id))
    .map(([id, qty]) => ({ product: byId.get(id)!, qty }));
  const total = lines.reduce(
    (sum, l) => sum + l.product.priceMinorUnits * l.qty,
    0,
  );
  const categories = [
    ...new Set(products.map((p) => p.category ?? t.bar.noCategory)),
  ];

  const add = (p: ProductView, delta: number) => {
    setError(undefined);
    setLast(undefined);
    setCart((c) => {
      const next = Math.max(0, Math.min(999, (c[p.productId] ?? 0) + delta));
      return { ...c, [p.productId]: next };
    });
  };

  const pay = async (method: PaymentMethod) => {
    if (lines.length === 0) return;
    setBusy(true);
    setError(undefined);
    try {
      const sale = await apiPost<SaleView>(
        `locations/${encodeURIComponent(location.locationId)}/sales`,
        {
          items: lines.map((l) => ({
            productId: l.product.productId,
            quantity: l.qty,
          })),
          method,
          clientId: method === "Balance" ? client?.clientId : null,
          idempotencyKey: key,
        },
      );
      setLast(sale);
      setCart({});
      setClient(null);
      setKey(newKey());
      onSold();
      sales.refresh();
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div className="flex flex-col gap-6">
      <div className="grid gap-6 lg:grid-cols-[minmax(0,3fr)_minmax(0,2fr)]">
        <Card title={t.bar.shelf}>
          {products.length === 0 ? (
            <EmptyState>{t.bar.noProducts}</EmptyState>
          ) : (
            <div className="flex flex-col gap-4" data-testid="bar-shelf">
              {categories.map((cat) => (
                <section key={cat}>
                  <h3 className="mb-2 text-xs font-semibold uppercase text-slate-500">
                    {cat}
                  </h3>
                  <div className="grid grid-cols-2 gap-2 sm:grid-cols-3">
                    {products
                      .filter((p) => (p.category ?? t.bar.noCategory) === cat)
                      .map((p) => {
                        const out =
                          p.trackStock &&
                          p.stockQuantity <= (cart[p.productId] ?? 0);
                        return (
                          <button
                            key={p.productId}
                            type="button"
                            data-testid="bar-product"
                            disabled={out}
                            onClick={() => add(p, 1)}
                            className="flex flex-col items-start rounded-lg border border-slate-200 bg-white p-3 text-left hover:border-brand-500 disabled:cursor-not-allowed disabled:opacity-50"
                          >
                            <span className="font-medium text-slate-900">
                              {p.name}
                            </span>
                            <span className="tabular-nums text-sm text-slate-700">
                              {formatMoney(
                                p.priceMinorUnits,
                                location.currency,
                              )}
                            </span>
                            {p.trackStock && (
                              <span
                                className={`text-xs ${p.stockQuantity <= 3 ? "text-amber-700" : "text-slate-500"}`}
                              >
                                {t.bar.inStock}: {p.stockQuantity}
                              </span>
                            )}
                          </button>
                        );
                      })}
                  </div>
                </section>
              ))}
            </div>
          )}
        </Card>
        <Card title={t.bar.cart}>
          <div className="flex flex-col gap-3" data-testid="bar-cart">
            {lines.length === 0 && (
              <p className="text-sm text-slate-500">{t.bar.cartEmpty}</p>
            )}
            {lines.map((l) => (
              <div
                key={l.product.productId}
                className="flex items-center gap-2 text-sm"
                data-testid="cart-line"
              >
                <span className="flex-1">{l.product.name}</span>
                <Button
                  variant="secondary"
                  className="px-2 py-0.5"
                  onClick={() => add(l.product, -1)}
                  aria-label={`${t.bar.less}: ${l.product.name}`}
                >
                  −
                </Button>
                <span className="w-8 text-center tabular-nums">{l.qty}</span>
                <Button
                  variant="secondary"
                  className="px-2 py-0.5"
                  disabled={
                    l.product.trackStock && l.qty >= l.product.stockQuantity
                  }
                  onClick={() => add(l.product, 1)}
                  aria-label={`${t.bar.more}: ${l.product.name}`}
                >
                  +
                </Button>
                <span className="w-24 text-right tabular-nums">
                  {formatMoney(
                    l.product.priceMinorUnits * l.qty,
                    location.currency,
                  )}
                </span>
              </div>
            ))}
            <div className="flex items-center justify-between border-t border-slate-200 pt-3">
              <span className="font-semibold">{t.bar.total}</span>
              <span
                className="text-xl font-bold tabular-nums"
                data-testid="cart-total"
              >
                {formatMoney(total, location.currency)}
              </span>
            </div>
            <div className="flex flex-wrap gap-2">
              <Button
                disabled={busy || lines.length === 0}
                onClick={() => pay("Cash")}
              >
                {t.cash.payCash}
              </Button>
              <Button
                variant="secondary"
                disabled={busy || lines.length === 0}
                onClick={() => pay("Card")}
              >
                {t.cash.payCard}
              </Button>
              <Button
                variant="secondary"
                disabled={busy || lines.length === 0 || !client}
                onClick={() => pay("Balance")}
              >
                {t.cash.payBalance}
              </Button>
              {lines.length > 0 && (
                <Button
                  variant="secondary"
                  disabled={busy}
                  onClick={() => setCart({})}
                >
                  {t.bar.clear}
                </Button>
              )}
            </div>
            <ClientPicker value={client} onChange={setClient} />
            <p className="text-xs text-slate-500">{t.bar.balanceHint}</p>
            {last && (
              <p
                role="status"
                className="rounded-lg bg-emerald-50 px-3 py-2 text-sm text-emerald-800"
                data-testid="sale-done"
              >
                {t.bar.sold}: {formatMoney(last.totalMinorUnits, last.currency)}{" "}
                · {t.cash.methods[last.method]}
              </p>
            )}
            {error && (
              <p
                role="alert"
                className="rounded-lg bg-red-50 px-3 py-2 text-sm text-red-800"
              >
                {error}
              </p>
            )}
          </div>
        </Card>
      </div>
      <SalesCard
        sales={sales.data}
        error={sales.error}
        onChanged={() => {
          sales.refresh();
          onSold();
        }}
      />
    </div>
  );
}

function SalesCard({
  sales,
  error,
  onChanged,
}: {
  sales?: SaleView[];
  error?: Error;
  onChanged: () => void;
}) {
  const { location } = useShell();
  return (
    <Card title={t.bar.sales}>
      {error && <ErrorState error={error} />}
      {sales && sales.length === 0 && <EmptyState>{t.bar.noSales}</EmptyState>}
      {sales && sales.length > 0 && (
        <div className="overflow-x-auto">
          <table className="w-full text-left text-sm" data-testid="sales-table">
            <thead className="text-xs uppercase text-slate-500">
              <tr>
                <th className="py-2 pr-4">{t.cash.when}</th>
                <th className="py-2 pr-4">{t.bar.items}</th>
                <th className="py-2 pr-4">{t.cash.method}</th>
                <th className="py-2 pr-4 text-right">{t.cash.amount}</th>
                <th className="py-2 pr-4">{t.cash.who}</th>
                <th className="py-2" />
              </tr>
            </thead>
            <tbody className="divide-y divide-slate-100">
              {sales.map((s) => (
                <tr
                  key={s.saleId}
                  data-testid="sale-row"
                  data-status={s.status}
                >
                  <td className="py-2 pr-4 whitespace-nowrap text-slate-600">
                    {formatTime(s.createdAtUtc, location.timezone)}
                  </td>
                  <td className="py-2 pr-4">
                    {s.items.map((i) => `${i.name} × ${i.quantity}`).join(", ")}
                    {s.clientName && (
                      <span className="block text-xs text-slate-500">
                        {s.clientName}
                      </span>
                    )}
                    {s.status === "Refunded" && (
                      <span className="block text-xs font-semibold text-red-700">
                        {t.bar.refunded}{" "}
                        {formatDateTime(s.refundedAtUtc, location.timezone)}:{" "}
                        {s.refundReason}
                      </span>
                    )}
                  </td>
                  <td className="py-2 pr-4">{t.cash.methods[s.method]}</td>
                  <td
                    className={`py-2 pr-4 text-right tabular-nums ${s.status === "Refunded" ? "text-slate-400 line-through" : ""}`}
                  >
                    {formatMoney(s.totalMinorUnits, s.currency)}
                  </td>
                  <td className="py-2 pr-4">{s.createdByName}</td>
                  <td className="py-2">
                    {s.status === "Paid" && (
                      <RefundSale sale={s} onDone={onChanged} />
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </Card>
  );
}

function RefundSale({ sale, onDone }: { sale: SaleView; onDone: () => void }) {
  const { can } = useShell();
  const [open, setOpen] = useState(false);
  const [reason, setReason] = useState("");
  const [key, setKey] = useState(newKey);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  if (!can("cash.refund")) return null;
  if (!open) {
    return (
      <Button
        variant="secondary"
        className="py-1"
        onClick={() => setOpen(true)}
      >
        {t.cash.refund}
      </Button>
    );
  }

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    setBusy(true);
    setError(undefined);
    try {
      await apiPost(`sales/${sale.saleId}/refund`, {
        reason,
        idempotencyKey: key,
      });
      setKey(newKey());
      setOpen(false);
      onDone();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <form
      onSubmit={submit}
      className="flex flex-col gap-2"
      aria-label={t.bar.refundTitle}
    >
      <input
        aria-label={t.cash.refundReason}
        placeholder={t.cash.refundReason}
        className={`${inputClass} py-1`}
        required
        minLength={3}
        maxLength={200}
        value={reason}
        onChange={(e) => setReason(e.target.value)}
      />
      <div className="flex gap-2">
        <Button type="submit" variant="danger" className="py-1" disabled={busy}>
          {t.bar.refundSale}
        </Button>
        <Button
          variant="secondary"
          className="py-1"
          onClick={() => setOpen(false)}
        >
          ✕
        </Button>
      </div>
      {error && <p className="text-xs text-red-700">{error}</p>}
    </form>
  );
}

function CatalogTab({
  products,
  onChanged,
}: {
  products: ProductView[];
  onChanged: () => void;
}) {
  return (
    <div className="flex flex-col gap-6">
      <ProductForm onSaved={onChanged} />
      <Card title={t.bar.products}>
        {products.length === 0 ? (
          <EmptyState>{t.bar.noProducts}</EmptyState>
        ) : (
          <div
            className="flex flex-col divide-y divide-slate-100"
            data-testid="catalog"
          >
            {products.map((p) => (
              <CatalogRow key={p.productId} product={p} onChanged={onChanged} />
            ))}
          </div>
        )}
      </Card>
    </div>
  );
}

function ProductForm({
  product,
  onSaved,
  onCancel,
}: {
  product?: ProductView;
  onSaved: () => void;
  onCancel?: () => void;
}) {
  const { location } = useShell();
  const [name, setName] = useState(product?.name ?? "");
  const [category, setCategory] = useState(product?.category ?? "");
  const [price, setPrice] = useState(
    product ? moneyText(product.priceMinorUnits) : "",
  );
  const [trackStock, setTrackStock] = useState(product?.trackStock ?? true);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const submit = async (e: FormEvent) => {
    e.preventDefault();
    const minor = parseMoney(price);
    if (minor === null) {
      setError(t.cash.badAmount);
      return;
    }
    setBusy(true);
    setError(undefined);
    try {
      const body = {
        name,
        category: category || null,
        priceMinorUnits: minor,
        trackStock,
        isActive: product?.isActive ?? true,
      };
      await apiPost(
        product
          ? `products/${product.productId}`
          : `locations/${encodeURIComponent(location.locationId)}/products`,
        body,
      );
      if (!product) {
        setName("");
        setPrice("");
      }
      onSaved();
      onCancel?.();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  const form = (
    <form
      onSubmit={submit}
      className="flex flex-col gap-3"
      aria-label={product ? `${t.bar.edit}: ${product.name}` : t.bar.newProduct}
    >
      <div className="grid gap-3 sm:grid-cols-4 sm:items-end">
        <Field label={t.bar.name}>
          <input
            className={inputClass}
            required
            maxLength={60}
            value={name}
            onChange={(e) => setName(e.target.value)}
          />
        </Field>
        <Field label={t.bar.category}>
          <input
            className={inputClass}
            maxLength={40}
            value={category}
            onChange={(e) => setCategory(e.target.value)}
          />
        </Field>
        <Field label={`${t.bar.price}, ${location.currency}`}>
          <input
            className={inputClass}
            required
            inputMode="decimal"
            value={price}
            onChange={(e) => setPrice(e.target.value)}
          />
        </Field>
        <label className="flex items-center gap-2 text-sm">
          <input
            type="checkbox"
            checked={trackStock}
            onChange={(e) => setTrackStock(e.target.checked)}
          />
          {t.bar.trackStock}
        </label>
      </div>
      <div className="flex gap-2">
        <Button type="submit" disabled={busy}>
          {product ? t.locations.save : t.bar.addProduct}
        </Button>
        {onCancel && (
          <Button variant="secondary" onClick={onCancel}>
            {t.mfa.cancel}
          </Button>
        )}
      </div>
      {error && (
        <p role="alert" className="text-sm text-red-700">
          {error}
        </p>
      )}
    </form>
  );
  return product ? form : <Card title={t.bar.newProduct}>{form}</Card>;
}

function CatalogRow({
  product,
  onChanged,
}: {
  product: ProductView;
  onChanged: () => void;
}) {
  const { location } = useShell();
  const [editing, setEditing] = useState(false);
  const [history, setHistory] = useState<StockMovementView[]>();
  const [kind, setKind] = useState<StockKind>("Receipt");
  const [qty, setQty] = useState("");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();

  const toggleActive = async () => {
    setError(undefined);
    try {
      await apiPost(`products/${product.productId}`, {
        ...product,
        isActive: !product.isActive,
      });
      onChanged();
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const loadHistory = async () => {
    if (history) {
      setHistory(undefined);
      return;
    }
    try {
      setHistory(
        await apiGet<StockMovementView[]>(
          `products/${product.productId}/movements`,
        ),
      );
    } catch (e) {
      setError((e as Error).message);
    }
  };

  const stock = async (e: FormEvent) => {
    e.preventDefault();
    const quantity = Number(qty);
    if (!Number.isInteger(quantity) || quantity < 0) {
      setError(t.bar.badQuantity);
      return;
    }
    setBusy(true);
    setError(undefined);
    try {
      await apiPost(`products/${product.productId}/stock`, {
        kind,
        quantity,
        reason: reason || null,
      });
      setQty("");
      setReason("");
      if (history)
        setHistory(
          await apiGet<StockMovementView[]>(
            `products/${product.productId}/movements`,
          ),
        );
      onChanged();
    } catch (err) {
      setError((err as Error).message);
    } finally {
      setBusy(false);
    }
  };

  return (
    <div
      className={`flex flex-col gap-3 py-3 ${product.isActive ? "" : "opacity-60"}`}
      data-testid="catalog-row"
    >
      {editing ? (
        <ProductForm
          product={product}
          onSaved={onChanged}
          onCancel={() => setEditing(false)}
        />
      ) : (
        <div className="flex flex-wrap items-center gap-3 text-sm">
          <span className="min-w-40 font-medium text-slate-900">
            {product.name}
            {product.category && (
              <span className="block text-xs font-normal text-slate-500">
                {product.category}
              </span>
            )}
          </span>
          <span className="tabular-nums">
            {formatMoney(product.priceMinorUnits, location.currency)}
          </span>
          <span className="text-slate-600" data-testid="catalog-stock">
            {product.trackStock
              ? `${t.bar.inStock}: ${product.stockQuantity}`
              : t.bar.noStock}
          </span>
          {!product.isActive && (
            <span className="rounded bg-slate-200 px-1.5 text-xs">
              {t.bar.inactive}
            </span>
          )}
          <div className="ml-auto flex gap-2">
            <Button
              variant="secondary"
              className="py-1"
              onClick={() => setEditing(true)}
            >
              {t.bar.edit}
            </Button>
            <Button variant="secondary" className="py-1" onClick={toggleActive}>
              {product.isActive ? t.bar.deactivate : t.bar.activate}
            </Button>
            {product.trackStock && (
              <Button
                variant="secondary"
                className="py-1"
                onClick={loadHistory}
              >
                {history ? t.tariffs.hide : t.bar.history}
              </Button>
            )}
          </div>
        </div>
      )}
      {product.trackStock && (
        <form
          onSubmit={stock}
          className="flex flex-wrap items-end gap-2"
          aria-label={`${t.bar.stock}: ${product.name}`}
        >
          <select
            aria-label={t.bar.stockKind}
            className={`${inputClass} py-1`}
            value={kind}
            onChange={(e) => setKind(e.target.value as StockKind)}
          >
            <option value="Receipt">{t.bar.stockKinds.Receipt}</option>
            <option value="WriteOff">{t.bar.stockKinds.WriteOff}</option>
            <option value="Count">{t.bar.stockKinds.Count}</option>
          </select>
          <input
            aria-label={kind === "Count" ? t.bar.counted : t.bar.quantity}
            placeholder={kind === "Count" ? t.bar.counted : t.bar.quantity}
            className={`${inputClass} w-28 py-1`}
            inputMode="numeric"
            required
            value={qty}
            onChange={(e) => setQty(e.target.value)}
          />
          <input
            aria-label={t.bar.reason}
            placeholder={
              kind === "Receipt" ? t.bar.reasonOptional : t.bar.reason
            }
            className={`${inputClass} w-48 py-1`}
            required={kind !== "Receipt"}
            minLength={kind === "Receipt" ? undefined : 3}
            maxLength={200}
            value={reason}
            onChange={(e) => setReason(e.target.value)}
          />
          <Button
            type="submit"
            variant="secondary"
            className="py-1"
            disabled={busy}
          >
            {t.bar.apply}
          </Button>
        </form>
      )}
      {error && (
        <p role="alert" className="text-sm text-red-700">
          {error}
        </p>
      )}
      {history && (
        <table className="w-full text-left text-xs" data-testid="stock-history">
          <tbody className="divide-y divide-slate-100">
            {history.map((m) => (
              <tr key={m.movementId}>
                <td className="py-1 pr-3 whitespace-nowrap text-slate-500">
                  {formatDateTime(m.createdAtUtc, location.timezone)}
                </td>
                <td className="py-1 pr-3">{t.bar.movementKinds[m.kind]}</td>
                <td
                  className={`py-1 pr-3 text-right tabular-nums ${m.quantity < 0 ? "text-red-700" : "text-emerald-700"}`}
                >
                  {m.quantity > 0 ? "+" : ""}
                  {m.quantity}
                </td>
                <td className="py-1 pr-3 text-right tabular-nums">
                  {m.quantityAfter}
                </td>
                <td className="py-1 pr-3">{m.reason}</td>
                <td className="py-1">{m.createdByName}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  );
}
