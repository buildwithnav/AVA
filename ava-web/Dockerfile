FROM node:22-alpine AS build
WORKDIR /app
ARG API_URL=http://localhost:5010
COPY package*.json ./
RUN npm ci
COPY . .
# Patch the production environment with the build-arg API_URL
RUN sed -i "s|http://localhost:5010|${API_URL}|g" src/environments/environment.ts
RUN npx ng build --configuration=production

FROM nginx:alpine AS runtime
COPY --from=build /app/dist/ava-web/browser /usr/share/nginx/html
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
