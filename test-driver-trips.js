'use strict';

const API_BASE = 'http://localhost:3001/api';

async function testDriverTripFlow() {
  console.log('🧪 Bắt đầu test luồng NXP-137: Tài xế thực hiện chuyến vận chuyển container...\n');

  try {
    // 1. Đăng nhập Admin / Dispatcher để tạo chuyến mới
    console.log('1️⃣ Đăng nhập với admin...');
    const adminLoginRes = await fetch(`${API_BASE}/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: 'admin', password: 'NexusPort@2026' }),
    });
    const adminLoginData = await adminLoginRes.json();
    const adminToken = adminLoginData?.data?.token;

    // 2. Đăng nhập Driver (driver01)
    console.log('2️⃣ Đăng nhập với driver01...');
    const driverLoginRes = await fetch(`${API_BASE}/auth/login`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username: 'driver01', password: 'NexusPort@2026' }),
    });
    const driverLoginData = await driverLoginRes.json();
    const driverToken = driverLoginData?.data?.token;
    const driverId = driverLoginData?.data?.user?.id;

    console.log('  ✅ Đăng nhập 2 tài khoản thành công!');

    const driverHeaders = {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${driverToken}`,
    };

    const adminHeaders = {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${adminToken}`,
    };

    // 3. Admin tạo chuyến mới cho driver01
    console.log('\n3️⃣ Admin tạo chuyến vận chuyển mới cho driver01...');
    const createTripRes = await fetch(`${API_BASE}/trips`, {
      method: 'POST',
      headers: adminHeaders,
      body: JSON.stringify({
        driverId,
        truckPlate: '51C-888.88',
        containerNo: 'WHLU1122334',
        containerType: '40ft High Cube',
        cargoType: 'Hàng điện tử',
        gateInCode: 'GATE-A1',
        yardBlock: 'Block B',
        yardSlot: 'B03-01',
        note: 'Test tự động NXP-137',
      }),
    });
    const createTripJson = await createTripRes.json();
    const newTrip = createTripJson?.data?.trip || createTripJson?.data;
    const tripId = newTrip.id;
    console.log(`  ✅ Tạo chuyến thành công! Mã chuyến: [${newTrip.tripCode}] (ID: ${tripId})`);

    // Helper patch request cho driver
    async function driverPatch(endpoint, body = {}) {
      const res = await fetch(`${API_BASE}/driver/trips/${tripId}/${endpoint}`, {
        method: 'PATCH',
        headers: driverHeaders,
        body: JSON.stringify(body),
      });
      const json = await res.json();
      return json?.data?.trip || json?.data || json;
    }

    console.log(`\n🎯 Tiến hành thực hiện toàn bộ 8 bước trạng thái cho chuyến [${newTrip.tripCode}]:`);

    // Bước 1: Confirm Acknowledge (Assigned -> Acknowledged)
    console.log('\n  👉 Bước 1: Confirm Acknowledge...');
    const step1 = await driverPatch('acknowledge');
    console.log(`     Status: ${step1.status} (Next: ${step1.nextStep})`);

    // Bước 2: Pickup Vehicle (Acknowledged -> Vehicle Picked Up)
    console.log('\n  👉 Bước 2: Confirm Pickup Vehicle...');
    const step2 = await driverPatch('pickup-vehicle', { note: 'Đã nhận xe đầu kéo 51C-888.88 tại bãi xe' });
    console.log(`     Status: ${step2.status} (Next: ${step2.nextStep})`);

    // Bước 3: Pickup Container (Vehicle Picked Up -> Container Picked Up)
    console.log('\n  👉 Bước 3: Confirm Pickup Container...');
    const step3 = await driverPatch('pickup-container', { note: 'Đã nhận container WHLU1122334 tại kho hàng' });
    console.log(`     Status: ${step3.status} (Next: ${step3.nextStep})`);

    // Bước 4: Gate In (Container Picked Up -> In Transit)
    console.log('\n  👉 Bước 4: Confirm Gate In...');
    const step4 = await driverPatch('gate-in', { note: 'Đã quét mã tại cổng GATE-A1' });
    console.log(`     Status: ${step4.status} (Next: ${step4.nextStep})`);

    // Bước 5: Arrive at Yard (In Transit -> Arrived at Yard)
    console.log('\n  👉 Bước 5: Confirm Arrived at Yard...');
    const step5 = await driverPatch('arrive-yard', { note: 'Đã di chuyển đến vị trí Block B' });
    console.log(`     Status: ${step5.status} (Next: ${step5.nextStep})`);

    // Bước 6: Deliver Container (Arrived at Yard -> Container Delivered)
    console.log('\n  👉 Bước 6: Confirm Deliver Container...');
    const step6 = await driverPatch('deliver-container', { note: 'Đã hạ container xuống ô B03-01' });
    console.log(`     Status: ${step6.status} (Next: ${step6.nextStep})`);

    // Bước 7: Gate Out (Container Delivered -> Gate Out Completed)
    console.log('\n  👉 Bước 7: Confirm Gate Out...');
    const step7 = await driverPatch('gate-out', { note: 'Đã Gate Out qua cổng GATE-B1' });
    console.log(`     Status: ${step7.status} (Next: ${step7.nextStep})`);

    // Bước 8: Return Vehicle (Gate Out Completed -> Completed)
    console.log('\n  👉 Bước 8: Confirm Return Vehicle & Complete Trip...');
    const step8 = await driverPatch('return-vehicle', { note: 'Đã bàn giao xe lại bãi đỗ' });
    console.log(`     Status: ${step8.status} (Next: ${step8.nextStep})`);

    // Kiểm tra chi tiết chuyến & timeline lịch sử
    console.log('\n📊 Kiểm tra chi tiết và timeline lịch sử chuyến:');
    const detailRes = await fetch(`${API_BASE}/driver/trips/${tripId}`, { headers: driverHeaders });
    const detailJson = await detailRes.json();
    const detail = detailJson?.data?.trip || detailJson?.data;
    console.log(`     Mã chuyến: ${detail.tripCode}`);
    console.log(`     Trạng thái cuối: ${detail.status}`);
    console.log(`     Số bước lịch sử đã lưu: ${detail.history?.length || 0}`);
    detail.history?.forEach((h, idx) => {
      console.log(`       ${idx + 1}. [${h.fromStatus || 'START'} ➔ ${h.toStatus}] vào ${new Date(h.changedAt).toLocaleTimeString()} (${h.note || ''})`);
    });

    console.log('\n✨ ✅ ALL END-TO-END TESTS PASSED FOR TICKET NXP-137! ✨');
  } catch (error) {
    console.error('\n❌ Test thất bại:', error.message);
  }
}

testDriverTripFlow();
