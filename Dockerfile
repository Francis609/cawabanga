FROM rust:1.100-alpine AS build
RUN apk add --no-cache musl-dev
WORKDIR /src
COPY Cargo.toml Cargo.lock ./
COPY src ./src
RUN cargo build --release --locked

FROM alpine:3.24
RUN apk add --no-cache ca-certificates
WORKDIR /app
COPY --from=build /src/target/release/cawabanga-api /usr/local/bin/cawabanga-api
ENV CAWABANGA_BIND=0.0.0.0:5207
ENV CAWABANGA_PROPOSAL_STORE=/data/proposals.json
EXPOSE 5207
VOLUME ["/data"]
ENTRYPOINT ["/usr/local/bin/cawabanga-api"]
