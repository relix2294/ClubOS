import { createHmac } from "node:crypto";

// TOTP (RFC 6238, SHA1, 30 с, 6 цифр) для e2e — тот же алгоритм, что у приложений-аутентификаторов.

function base32Decode(text: string): Buffer {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let buffer = 0;
  let bits = 0;
  const out: number[] = [];
  for (const c of text.replace(/=+$/, "").toUpperCase()) {
    const value = alphabet.indexOf(c);
    if (value < 0) throw new Error(`base32: ${c}`);
    buffer = (buffer << 5) | value;
    bits += 5;
    if (bits >= 8) {
      out.push((buffer >> (bits - 8)) & 0xff);
      bits -= 8;
    }
  }
  return Buffer.from(out);
}

/** Код для шага «сейчас + stepOffset» (шаг = 30 с). */
export function totp(secret: string, stepOffset = 0, now = Date.now()): string {
  const step = Math.floor(now / 1000 / 30) + stepOffset;
  const counter = Buffer.alloc(8);
  counter.writeBigInt64BE(BigInt(step));
  const hash = createHmac("sha1", base32Decode(secret)).update(counter).digest();
  const offset = hash[hash.length - 1] & 0x0f;
  const binary = ((hash[offset] & 0x7f) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
  return String(binary % 1_000_000).padStart(6, "0");
}
