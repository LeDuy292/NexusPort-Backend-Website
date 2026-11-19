'use strict';

/**
 * Ảnh bằng chứng (hư hỏng container, ...) đến dưới dạng data URL base64, mỗi ảnh vài trăm KB.
 * Đẩy lên S3 và chỉ giữ khóa đối tượng trong DB; khi trả cho client thì ký lại thành link có hạn.
 * Nếu S3 chưa cấu hình hoặc lỗi thì giữ nguyên dữ liệu gốc để luồng nghiệp vụ không bị gián đoạn.
 */

const crypto = require('crypto');
const { S3Client, PutObjectCommand, GetObjectCommand } = require('@aws-sdk/client-s3');
const { getSignedUrl } = require('@aws-sdk/s3-request-presigner');

const region = process.env.AWS_REGION || process.env.AWS__Region || 'us-east-1';
const bucket = process.env.AWS_BUCKET_NAME || process.env.AWS__BucketName;
const hasCredentials = Boolean(
  (process.env.AWS_ACCESS_KEY_ID || process.env.AWS__AccessKey) && (process.env.AWS_SECRET_ACCESS_KEY || process.env.AWS__SecretKey),
);

let client;
function s3() {
  if (!client) {
    client = new S3Client({
      region,
      credentials: hasCredentials
        ? {
            accessKeyId: process.env.AWS_ACCESS_KEY_ID || process.env.AWS__AccessKey,
            secretAccessKey: process.env.AWS_SECRET_ACCESS_KEY || process.env.AWS__SecretKey,
          }
        : undefined,
    });
  }
  return client;
}

const DATA_URL = /^data:(image\/[a-zA-Z0-9.+-]+);base64,(.+)$/s;
const KEY_PREFIX = 'NexusPort/';

async function storeDataUrl(value, folder) {
  if (typeof value !== 'string' || !bucket) return value;
  const m = DATA_URL.exec(value);
  if (!m) return value;
  try {
    const ext = m[1].endsWith('png') ? '.png' : '.jpg';
    const key = `${KEY_PREFIX}${folder}/${crypto.randomUUID().replace(/-/g, '')}${ext}`;
    await s3().send(new PutObjectCommand({ Bucket: bucket, Key: key, Body: Buffer.from(m[2], 'base64'), ContentType: m[1] }));
    return key;
  } catch (err) {
    console.warn('[EvidenceStorage] Không đẩy được ảnh lên S3, giữ dữ liệu gốc:', err.message);
    return value;
  }
}

async function storeAll(values, folder) {
  return Promise.all((Array.isArray(values) ? values : []).map((v) => storeDataUrl(v, folder)));
}

async function link(keyOrUrl, expiresInSeconds = 7 * 24 * 3600) {
  if (typeof keyOrUrl !== 'string' || !keyOrUrl.startsWith(KEY_PREFIX) || !bucket) return keyOrUrl;
  return getSignedUrl(s3(), new GetObjectCommand({ Bucket: bucket, Key: keyOrUrl }), { expiresIn: expiresInSeconds });
}

module.exports = { storeDataUrl, storeAll, link };
