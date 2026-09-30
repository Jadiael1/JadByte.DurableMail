import http from 'k6/http';
import { sleep, check } from 'k6';

export const options = {
    stages: [
        { duration: '30s', target: 50 },  // Escala para 50 usuários simultâneos em 30s
        { duration: '1m', target: 50 },   // Mantém 50 usuários disparando requisições por 1 min
        { duration: '10s', target: 0 },   // Desacelera a carga
    ],
};

export default function () {
    const url = 'http://localhost:8080/v1/signUp'; // Endpoint conforme seu IdentityEndpoints.cs
    
    const payload = JSON.stringify({
        email: `teste_${__VU}_${__ITER}@example.com`,
        password: 'Password123!'
    });

    const params = {
        headers: { 'Content-Type': 'application/json' },
    };

    const res = http.post(url, payload, params);

    check(res, {
        'status 200': (r) => r.status === 200,
    });

    sleep(1);
}