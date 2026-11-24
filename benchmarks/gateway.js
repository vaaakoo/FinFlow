import http from 'k6/http';
import { check, sleep } from 'k6';

export const options = {
  vus: 5,
  duration: '15s',
  thresholds: { http_req_failed: ['rate<0.01'], http_req_duration: ['p(95)<200'] }
};

export default function () {
  const base = __ENV.FINFLOW_BASE_URL || 'http://gateway:8080';
  const response = http.get(base + '/health/live', { tags: { name: 'gateway health' } });
  check(response, { 'ready response': result => result.status === 200 });
  sleep(0.05);
}
