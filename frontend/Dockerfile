FROM node:24-alpine AS build
WORKDIR /portal
COPY package*.json ./
RUN npm ci
COPY . .
RUN npm run build

FROM nginx:1.28-alpine
COPY --from=build /portal/dist/portal/browser /usr/share/nginx/html
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
