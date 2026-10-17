FROM node:22-bookworm-slim@sha256:c3de60bf2f9dd0ac6370e6117950ff62d6e339527e7472301c9c78a017978392
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci --omit=dev --ignore-scripts && npm cache clean --force
COPY server.mjs voice-hub.mjs ./
COPY worker ./worker
COPY web ./web
ENV PORT=3000 NODE_ENV=production
EXPOSE 3000
USER node
CMD ["node", "server.mjs"]
