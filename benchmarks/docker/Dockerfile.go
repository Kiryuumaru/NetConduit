FROM golang:1.23-bookworm AS build
WORKDIR /src
COPY benchmarks/docker/go-comparison/go.mod .
COPY benchmarks/docker/go-comparison/go.sum .
COPY benchmarks/docker/go-comparison/main.go .
RUN go mod download
RUN CGO_ENABLED=0 go build -o /bench .

FROM debian:bookworm-slim
COPY --from=build /bench /bench
ENTRYPOINT ["/bench"]
