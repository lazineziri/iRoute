#!/usr/bin/env sh
set -eu

build_directory="target/example-classes"
mkdir -p "$build_directory"
javac --release 25 -d "$build_directory" \
  src/main/java/dev/iroute/IRouteClient.java \
  ../../examples/sdks/java/ExecuteExample.java
java -cp "$build_directory" ExecuteExample
